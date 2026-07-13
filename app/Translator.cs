using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LlmTranslate;

/// <summary>
/// Translates a markdown/text document while preserving math ($...$, $$...$$),
/// code (fenced ``` and inline `), and markdown structure. Protected spans are
/// replaced with sentinel placeholders, the prose is translated in chunks by the
/// LLM, then placeholders are restored verbatim.
///
/// Resilience: each chunk is retried on transient errors; translated chunks are
/// checkpointed to a "&lt;name&gt;.progress" folder so an interrupted run resumes
/// instead of starting over. Optional duty-cycle pacing lowers sustained GPU load.
/// </summary>
public sealed class Translator
{
    private readonly AppConfig _cfg;
    private readonly OpenAiClient _client;
    private readonly Action<string> _log;
    private readonly GpuMonitor _gpu;
    private int _maxTokens;   // effective per-chunk output cap (config, clamped to server n_ctx)

    public Translator(AppConfig cfg, OpenAiClient client, Action<string> log)
    {
        _cfg = cfg;
        _client = client;
        _log = log;
        _gpu = new GpuMonitor(cfg.NvidiaSmiPath);
    }

    private static readonly (string name, Regex rx)[] Protectors =
    {
        ("fenced", new Regex(@"```[\s\S]*?```", RegexOptions.Compiled)),
        ("dispmath", new Regex(@"\$\$[\s\S]*?\$\$", RegexOptions.Compiled)),
        ("inlinecode", new Regex(@"`[^`\n]+`", RegexOptions.Compiled)),
        ("inlinemath", new Regex(@"\$[^$\n]+\$", RegexOptions.Compiled)),
    };

    private static string Ph(int i) => $"[[PH{i}]]";
    private static readonly Regex PhRx = new(@"\[\[PH(\d+)\]\]", RegexOptions.Compiled);

    /// <param name="targetTempC">Live provider of the target max GPU temperature (°C);
    /// read fresh each chunk so the slider can be moved during a running translation.</param>
    public async Task<string> TranslateDocumentAsync(
        string text, string? progressDir, Func<int> targetTempC,
        IProgress<double> progress, CancellationToken ct)
    {
        var store = new List<string>();
        string masked = Protect(text, store);
        var chunks = SplitIntoChunks(masked, _cfg.MaxCharsPerChunk);
        _log($"Защищено фрагментов (формулы/код): {store.Count}; чанков: {chunks.Count}");

        // per-chunk output cap from config, clamped to the server's context window (n_ctx)
        _maxTokens = _cfg.MaxTokens;
        int? nctx = await _client.GetServerMaxContextAsync();
        if (nctx.HasValue && _maxTokens > nctx.Value)
        {
            _log($"MaxTokens {_cfg.MaxTokens} превышает лимит сервера (n_ctx={nctx.Value}) — занижаю до {nctx.Value}.");
            _maxTokens = nctx.Value;
        }

        // ---- checkpoint setup ----
        string hash = Sha256(text);
        if (progressDir != null)
            PrepareProgressDir(progressDir, hash, chunks.Count);

        int reused = 0;
        var outParts = new string[chunks.Count];

        // Background thermal throttle: holds temperature by suspending/resuming the GPU compute
        // process (llama-server) purely by temperature — no per-chunk time deadline.
        // targetTempC() is read live, so the slider applies mid-run.
        var throttle = new GpuThrottle(_gpu, targetTempC, _log);
        throttle.Start();
        try
        {
        for (int i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            string? partPath = progressDir == null ? null : Path.Combine(progressDir, $"{i:D5}.part");
            if (partPath != null && File.Exists(partPath))
            {
                outParts[i] = await File.ReadAllTextAsync(partPath, ct);
                reused++;
                progress.Report((double)(i + 1) / chunks.Count);
                continue;
            }

            _log($"[{i + 1}/{chunks.Count}] перевод…");
            string translated = await TranslateChunkWithRetryAsync(chunks[i], i + 1, ct);

            outParts[i] = translated;
            if (partPath != null)
                await File.WriteAllTextAsync(partPath, translated, ct);

            progress.Report((double)(i + 1) / chunks.Count);

            // informational: log GPU temperature each chunk (the actual holding is done by the
            // background GpuThrottle, which suspends the server process while it is too hot).
            int? temp = await _gpu.ReadTempAsync(ct);
            _log(temp.HasValue
                ? $"   температура GPU: {temp}°C (цель ≤ {targetTempC()}°C)"
                : "   температура GPU: н/д (nvidia-smi недоступен)");
        }
        }
        finally { throttle.Stop(); }

        if (reused > 0) _log($"Из чекпоинта переиспользовано чанков: {reused}");

        string joined = string.Join("\n\n", outParts);
        string restored = Restore(joined, store, out int missing);
        if (missing > 0)
            _log($"⚠ {missing} защищённых фрагментов не вернулись (модель потеряла плейсхолдер).");

        return restored.TrimEnd() + "\n";
    }

    private async Task<string> TranslateChunkWithRetryAsync(string chunk, int idx, CancellationToken ct)
    {
        int attempts = Math.Max(1, _cfg.RetryCount);
        for (int a = 1; ; a++)
        {
            try
            {
                string raw = await _client.ChatAsync(BuildSystemPrompt(), chunk, _maxTokens, ct);
                return StripThink(raw).Trim();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // real user cancel — do not retry
            }
            catch (Exception ex) when (a < attempts)
            {
                int backoff = 1500 * a; // 1.5s, 3s, 4.5s, ...
                _log($"   [{idx}] ошибка запроса ({Short(ex.Message)}); повтор {a}/{attempts - 1} через {backoff / 1000.0:0.0} c");
                await Task.Delay(backoff, ct);
            }
        }
    }

    private string BuildSystemPrompt() =>
        $"You are a professional technical translator. Translate the text from {_cfg.SourceLang} to {_cfg.TargetLang}.\n" +
        "STRICT RULES:\n" +
        "1. Translate ALL natural-language text, INCLUDING text inside table cells — both header cells and word values. Do not leave English words untranslated.\n" +
        "2. Keep the markdown structure identical: headings (#), lists, and tables — keep every | and --- separator and the same number of columns and rows.\n" +
        "3. Placeholder tokens of the form [[PHn]] (n is a number) are protected content — copy them EXACTLY, do not translate, move, or alter them.\n" +
        "4. Do NOT change: numbers, code, variable names, identifiers, units, or anything inside placeholders.\n" +
        "5. Output ONLY the translated document — no comments, no explanations, no code fences around the answer.\n" +
        "/no_think";

    // ---------- checkpoint helpers ----------

    private record ProgressMeta(string hash, int count, int maxChars);

    private void PrepareProgressDir(string dir, string hash, int count)
    {
        string metaPath = Path.Combine(dir, "meta.json");
        if (Directory.Exists(dir) && File.Exists(metaPath))
        {
            try
            {
                var meta = JsonSerializer.Deserialize<ProgressMeta>(File.ReadAllText(metaPath));
                if (meta != null && meta.hash == hash && meta.count == count && meta.maxChars == _cfg.MaxCharsPerChunk)
                {
                    _log($"Найден чекпоинт — продолжаю с места обрыва ({dir}).");
                    return; // reuse existing parts
                }
                _log("Чекпоинт устарел (документ/настройки изменились) — начинаю заново.");
            }
            catch { /* fall through to fresh */ }
            try { Directory.Delete(dir, true); } catch { }
        }
        Directory.CreateDirectory(dir);
        File.WriteAllText(metaPath, JsonSerializer.Serialize(new ProgressMeta(hash, count, _cfg.MaxCharsPerChunk)));
    }

    private static string Sha256(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes);
    }

    private static string Short(string s) => s.Length <= 80 ? s : s[..80];

    // ---------- protect / restore / split ----------

    private static string Protect(string text, List<string> store)
    {
        foreach (var (_, rx) in Protectors)
            text = rx.Replace(text, m => { store.Add(m.Value); return Ph(store.Count - 1); });
        return text;
    }

    private static string Restore(string text, List<string> store, out int missing)
    {
        var seen = new HashSet<int>();
        string result = PhRx.Replace(text, m =>
        {
            int idx = int.Parse(m.Groups[1].Value);
            if (idx >= 0 && idx < store.Count) { seen.Add(idx); return store[idx]; }
            return m.Value;
        });
        missing = store.Count - seen.Count;
        return result;
    }

    private static List<string> SplitIntoChunks(string text, int maxChars)
    {
        var blocks = Regex.Split(text, @"\r?\n\s*\r?\n");
        var chunks = new List<string>();
        var sb = new StringBuilder();

        foreach (var raw in blocks)
        {
            var block = raw.Trim('\r', '\n');
            if (block.Length == 0) continue;

            if (sb.Length > 0 && sb.Length + block.Length + 2 > maxChars)
            {
                chunks.Add(sb.ToString());
                sb.Clear();
            }

            if (block.Length > maxChars)
            {
                if (sb.Length > 0) { chunks.Add(sb.ToString()); sb.Clear(); }
                chunks.Add(block); // oversized single block (never split a paragraph)
                continue;
            }

            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(block);
        }
        if (sb.Length > 0) chunks.Add(sb.ToString());
        return chunks;
    }

    private static readonly Regex ThinkRx =
        new(@"<think>[\s\S]*?</think>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string StripThink(string s) => ThinkRx.Replace(s, "").TrimStart();
}
