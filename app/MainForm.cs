using System;
using System.Drawing;
using System.IO;
using System.Linq;
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
    private readonly ListBox _lstIn = new();
    private readonly Button _btnPick = new();
    private readonly Button _btnRemove = new();
    private readonly Button _btnClear = new();
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
        Width = 860;
        Height = 660;
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

        // input files (batch)
        y += 40;
        AddLabel("Файлы на вход (md / txt):", x, y);
        y += 20;
        _lstIn.SetBounds(x, y, w, 110);
        _lstIn.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _lstIn.HorizontalScrollbar = true;
        _lstIn.SelectionMode = SelectionMode.MultiExtended;
        _lstIn.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };
        Controls.Add(_lstIn);

        y += 114;
        _btnPick.SetBounds(x, y, 160, 28);
        _btnPick.Text = "Выбрать файлы…";
        _btnPick.Click += (_, _) => PickFiles();
        Controls.Add(_btnPick);

        _btnRemove.SetBounds(x + 170, y, 170, 28);
        _btnRemove.Text = "Удалить выделенный";
        _btnRemove.Click += (_, _) => RemoveSelected();
        Controls.Add(_btnRemove);

        _btnClear.SetBounds(x + 350, y, 120, 28);
        _btnClear.Text = "Очистить всё";
        _btnClear.Click += (_, _) => _lstIn.Items.Clear();
        Controls.Add(_btnClear);

        var hint = new Label
        {
            Left = x + 480, Top = y + 6, AutoSize = true, ForeColor = Color.Gray,
            Text = $"выход: <имя>{_cfg.OutputSuffix} рядом; готовые пропускаются"
        };
        Controls.Add(hint);

        // GPU target-temperature slider
        y += 40;
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
        _btnTranslate.SetBounds(x, y, 200, 32);
        _btnTranslate.Text = "Перевести пачку → RU";
        _btnTranslate.Click += async (_, _) => await TranslateAsync();
        Controls.Add(_btnTranslate);

        _btnCancel.SetBounds(x + 210, y, 110, 32);
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

    private void PickFiles()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Выберите файлы для перевода",
            Multiselect = true,
            Filter = "Markdown/текст (*.md;*.markdown;*.txt)|*.md;*.markdown;*.txt|Все файлы (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        foreach (var f in dlg.FileNames)
            if (!_lstIn.Items.Contains(f))
                _lstIn.Items.Add(f);
    }

    private void RemoveSelected()
    {
        foreach (int i in _lstIn.SelectedIndices.Cast<int>().OrderByDescending(i => i))
            _lstIn.Items.RemoveAt(i);
    }

    private string OutputPathFor(string input)
    {
        string dir = Path.GetDirectoryName(input) ?? ".";
        string name = Path.GetFileNameWithoutExtension(input);
        string ext = Path.GetExtension(input);
        return Path.Combine(dir, name + _cfg.OutputSuffix + ext);
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
            _lampText.Text = up ? $"{_cfg.BaseUrl}: {model}" : $"{_cfg.BaseUrl}: нет связи";
            UpdateGpuLabel();
        }
        if (InvokeRequired) BeginInvoke(Apply); else Apply();
    }

    private void SetProgress(double p)
    {
        int v = Math.Clamp((int)Math.Round(p * 1000), 0, 1000);
        if (InvokeRequired) BeginInvoke(() => _progress.Value = v); else _progress.Value = v;
    }

    private async Task TranslateAsync()
    {
        if (_busy) return;

        var files = _lstIn.Items.Cast<string>().ToList();
        if (files.Count == 0)
        {
            MessageBox.Show(this, "Добавьте хотя бы один файл.", "LLM Translate",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string? model = await _client.GetModelAsync();
        if (model == null)
        {
            MessageBox.Show(this,
                $"Модель на {_cfg.BaseUrl} недоступна.\nЗапусти сервер перевода (full-server-9b-translate.cmd) и дождись зелёной лампы.",
                "LLM Translate", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetBusy(true);
        SetProgress(0);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Log($"=== Старт: {files.Count} файлов, модель {model} ===");

        int ok = 0, skipped = 0, failed = 0;
        var failedNames = new System.Collections.Generic.List<string>();

        try
        {
            var tr = new Translator(_cfg, _client, Log);

            for (int i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string inp = files[i];
                string name = Path.GetFileName(inp);
                string outPath = OutputPathFor(inp);
                Log($"[{i + 1}/{files.Count}] {name} → {Path.GetFileName(outPath)}");

                if (!File.Exists(inp))
                {
                    Log("   ✗ файл не найден"); failed++; failedNames.Add(name);
                    SetProgress((double)(i + 1) / files.Count);
                    continue;
                }
                if (File.Exists(outPath) && new FileInfo(outPath).Length > 0)
                {
                    Log("   уже готово, пропускаю"); skipped++;
                    SetProgress((double)(i + 1) / files.Count);
                    continue;
                }

                int fileIndex = i;
                try
                {
                    string src = await File.ReadAllTextAsync(inp, ct);
                    string progressDir = Path.Combine(
                        Path.GetDirectoryName(inp)!, Path.GetFileNameWithoutExtension(inp) + ".progress");

                    var progress = new Progress<double>(p => SetProgress((fileIndex + p) / files.Count));
                    string result = await tr.TranslateDocumentAsync(src, progressDir, () => _targetTempValue, progress, ct);
                    await File.WriteAllTextAsync(outPath, result, ct);
                    try { if (Directory.Exists(progressDir)) Directory.Delete(progressDir, true); } catch { }
                    Log($"   ✓ {outPath}");
                    ok++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // user cancel — stop the whole batch
                }
                catch (Exception ex)
                {
                    Log($"   ✗ ошибка: {ex.Message}"); failed++; failedNames.Add(name);
                }

                SetProgress((double)(i + 1) / files.Count);
            }

            Log($"=== Готово: успешно {ok}, пропущено {skipped}, с ошибками {failed} ===");
            if (failed > 0) Log("Сбойные: " + string.Join("; ", failedNames));
        }
        catch (OperationCanceledException)
        {
            Log($"=== Отменено пользователем (успешно {ok}, пропущено {skipped}) ===");
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
        _btnPick.Enabled = !on;
        _btnRemove.Enabled = !on;
        _btnClear.Enabled = !on;
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
