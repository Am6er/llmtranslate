using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LlmTranslate;

public sealed class MainForm : Form
{
    private readonly AppConfig _cfg;
    private readonly OpenAiClient _client;
    private readonly System.Windows.Forms.Timer _healthTimer;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private volatile int _targetTempValue = 75; // live target max temp (°C) read by the translator
    private volatile int _lastTemp = -1;        // last GPU temp (°C) read by the timer, -1 = unknown
    private readonly GpuMonitor _gpuMon;

    private readonly Panel _lamp = new();
    private readonly Label _lampText = new();
    private readonly TextBox _txtIn = new();
    private readonly Label _lblOut = new();
    private readonly TrackBar _gpuLoad = new();
    private readonly Label _gpuLoadLabel = new();
    private readonly Button _btnTranslate = new();
    private readonly Button _btnCancel = new();
    private readonly ProgressBar _progress = new();
    private readonly TextBox _log = new();

    public MainForm()
    {
        _cfg = AppConfig.Load();
        _client = new OpenAiClient(_cfg);
        _gpuMon = new GpuMonitor(_cfg.NvidiaSmiPath);
        _targetTempValue = Math.Clamp(_cfg.TargetTempC, 50, 90);

        Text = "LLM Translate — EN→RU (локальная модель)";
        Width = 820;
        Height = 560;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        BuildUi();

        _healthTimer = new System.Windows.Forms.Timer { Interval = 2500 };
        _healthTimer.Tick += async (_, _) => await RefreshLampAsync();
        _healthTimer.Start();

        FormClosing += (_, _) => { try { _cts?.Cancel(); } catch { } _healthTimer.Stop(); _client.Dispose(); };
        _ = RefreshLampAsync();
    }

    private void BuildUi()
    {
        int x = 16, y = 14, w = Width - 40;

        // status: lamp + endpoint/model
        _lamp.SetBounds(x, y + 2, 16, 16);
        _lamp.BackColor = Color.Firebrick;
        _lamp.BorderStyle = BorderStyle.FixedSingle;
        _lampText.SetBounds(x + 24, y, w - 24, 22);
        _lampText.Text = $"{_cfg.BaseUrl}: нет связи";
        Controls.Add(_lamp);
        Controls.Add(_lampText);

        // input file
        y += 40;
        AddLabel("Файл на вход (md / txt):", x, y);
        y += 20;
        _txtIn.SetBounds(x, y, w - 110, 24);
        _txtIn.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _txtIn.TextChanged += (_, _) => UpdateOutLabel();
        var bIn = new Button { Text = "Выбрать…", Left = x + w - 100, Top = y - 1, Width = 90, Height = 26,
                               Anchor = AnchorStyles.Top | AnchorStyles.Right };
        bIn.Click += (_, _) => PickFile();
        Controls.Add(_txtIn);
        Controls.Add(bIn);

        // output (auto _RU)
        y += 32;
        _lblOut.SetBounds(x, y, w, 22);
        _lblOut.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _lblOut.ForeColor = Color.DimGray;
        _lblOut.Text = $"Выход: (файл + суффикс {_cfg.OutputSuffix} рядом с исходным)";
        Controls.Add(_lblOut);

        // GPU target-temperature slider (cool down between chunks if hotter)
        y += 30;
        _gpuLoadLabel.SetBounds(x, y + 4, 300, 20);
        Controls.Add(_gpuLoadLabel);
        _gpuLoad.SetBounds(x + 305, y, w - 305, 40);
        _gpuLoad.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _gpuLoad.Minimum = 50; _gpuLoad.Maximum = 90;
        _gpuLoad.TickFrequency = 5; _gpuLoad.LargeChange = 5; _gpuLoad.SmallChange = 1;
        _gpuLoad.Value = Math.Clamp(_cfg.TargetTempC, 50, 90);
        _targetTempValue = _gpuLoad.Value;
        _gpuLoad.Scroll += (_, _) =>
        {
            _targetTempValue = _gpuLoad.Value;     // live: affects the running translation
            UpdateGpuLabel();
            _cfg.TargetTempC = _gpuLoad.Value;
            try { _cfg.Save(); } catch { }
        };
        Controls.Add(_gpuLoad);
        UpdateGpuLabel();

        // translate / cancel
        y += 46;
        _btnTranslate.SetBounds(x, y, 180, 32);
        _btnTranslate.Text = "Перевести → RU";
        _btnTranslate.Click += async (_, _) => await TranslateAsync();
        Controls.Add(_btnTranslate);

        _btnCancel.SetBounds(x + 190, y, 110, 32);
        _btnCancel.Text = "Отмена";
        _btnCancel.Enabled = false;
        _btnCancel.Click += (_, _) => _cts?.Cancel();
        Controls.Add(_btnCancel);

        // progress
        y += 40;
        _progress.SetBounds(x, y, w, 20);
        _progress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _progress.Minimum = 0; _progress.Maximum = 1000;
        Controls.Add(_progress);

        // log
        y += 30;
        _log.SetBounds(x, y, w, Height - y - 56);
        _log.Multiline = true; _log.ReadOnly = true; _log.ScrollBars = ScrollBars.Vertical;
        _log.BackColor = Color.White; _log.Font = new Font("Consolas", 8.5f);
        _log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(_log);
    }

    private void AddLabel(string text, int x, int y)
        => Controls.Add(new Label { Text = text, Left = x, Top = y, AutoSize = true });

    private void UpdateGpuLabel()
    {
        string cur = _lastTemp >= 0 ? $"сейчас {_lastTemp}°C" : "сейчас —";
        string tgt = _targetTempValue >= 90 ? "без ограничений" : $"≤ {_targetTempValue}°C";
        _gpuLoadLabel.Text = $"Держать GPU {tgt}   ({cur})";
    }

    private void PickFile()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Выберите файл для перевода",
            Multiselect = false,
            Filter = "Markdown/текст (*.md;*.markdown;*.txt)|*.md;*.markdown;*.txt|Все файлы (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _txtIn.Text = dlg.FileName;
    }

    private string OutputPathFor(string input)
    {
        string dir = Path.GetDirectoryName(input) ?? ".";
        string name = Path.GetFileNameWithoutExtension(input);
        string ext = Path.GetExtension(input);
        return Path.Combine(dir, name + _cfg.OutputSuffix + ext);
    }

    private void UpdateOutLabel()
    {
        string inp = _txtIn.Text.Trim();
        _lblOut.Text = string.IsNullOrWhiteSpace(inp) || !File.Exists(inp)
            ? $"Выход: (файл + суффикс {_cfg.OutputSuffix} рядом с исходным)"
            : "Выход: " + OutputPathFor(inp);
    }

    private async Task RefreshLampAsync()
    {
        string? model = await _client.GetModelAsync();
        int? t = await _gpuMon.ReadTempAsync();
        if (t.HasValue) _lastTemp = t.Value;
        if (IsDisposed) return;
        void Apply()
        {
            bool up = model != null;
            _lamp.BackColor = up ? Color.ForestGreen : Color.Firebrick;
            _lampText.Text = up
                ? $"{_cfg.BaseUrl}: {model}"
                : $"{_cfg.BaseUrl}: нет связи";
            UpdateGpuLabel();
        }
        if (InvokeRequired) BeginInvoke(Apply); else Apply();
    }

    private async Task TranslateAsync()
    {
        if (_busy) return;

        string inp = _txtIn.Text.Trim();
        if (!File.Exists(inp))
        {
            MessageBox.Show(this, "Выберите существующий файл.", "LLM Translate",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string? model = await _client.GetModelAsync();
        if (model == null)
        {
            MessageBox.Show(this, $"Модель на {_cfg.BaseUrl} недоступна.\nЗапусти сервер перевода (full-server-9b-translate.cmd) и дождись зелёной лампы.",
                "LLM Translate", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string outPath = OutputPathFor(inp);
        SetBusy(true);
        _progress.Value = 0;
        _cts = new CancellationTokenSource();
        Log($"=== Перевод: {Path.GetFileName(inp)} → {Path.GetFileName(outPath)} (модель: {model}) ===");

        try
        {
            string src = await File.ReadAllTextAsync(inp, _cts.Token);
            string progressDir = Path.Combine(
                Path.GetDirectoryName(inp)!, Path.GetFileNameWithoutExtension(inp) + ".progress");

            var tr = new Translator(_cfg, _client, Log);
            var progress = new Progress<double>(p =>
            {
                int v = Math.Clamp((int)Math.Round(p * 1000), 0, 1000);
                if (InvokeRequired) BeginInvoke(() => _progress.Value = v); else _progress.Value = v;
            });

            string result = await tr.TranslateDocumentAsync(src, progressDir, () => _targetTempValue, progress, _cts.Token);
            await File.WriteAllTextAsync(outPath, result, _cts.Token);
            try { if (Directory.Exists(progressDir)) Directory.Delete(progressDir, true); } catch { }
            Log($"=== Готово → {outPath} (чекпоинт очищен) ===");
        }
        catch (OperationCanceledException) when (_cts?.IsCancellationRequested == true)
        {
            Log("=== Отменено пользователем ===");
        }
        catch (OperationCanceledException)
        {
            Log($"✗ Таймаут запроса к модели (>{_cfg.RequestTimeoutSec} с на чанк). " +
                "Уменьши MaxCharsPerChunk в config.json или проверь, что модель на :8080 отвечает.");
        }
        catch (Exception ex)
        {
            Log("✗ Ошибка: " + ex.Message);
            MessageBox.Show(this, ex.Message, "LLM Translate", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cts?.Dispose(); _cts = null;
            SetBusy(false);
        }
    }

    private void SetBusy(bool on)
    {
        _busy = on;
        _btnTranslate.Enabled = !on;
        _btnCancel.Enabled = on;
    }

    private void Log(string msg)
    {
        string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg + Environment.NewLine;
        if (_log.IsDisposed) return;
        if (_log.InvokeRequired) _log.BeginInvoke(() => AppendLog(line));
        else AppendLog(line);
    }

    private void AppendLog(string line)
    {
        _log.AppendText(line);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }
}
