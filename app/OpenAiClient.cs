using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LlmTranslate;

/// <summary>Minimal client for the llama.cpp OpenAI-compatible endpoints.</summary>
public sealed class OpenAiClient : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly HttpClient _probe = new() { Timeout = TimeSpan.FromSeconds(4) };
    private readonly HttpClient _chat;

    public OpenAiClient(AppConfig cfg)
    {
        _cfg = cfg;
        // No wall-clock timeout: the thermal throttle may SUSPEND the server mid-request while it
        // cools, which must not trip a spurious timeout. Real connection drops still throw
        // (→ retry), and the user can Cancel. (RequestTimeoutSec kept for compatibility, unused here.)
        _chat = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    /// <summary>Returns the served model id, or null if the endpoint is not reachable.</summary>
    public async Task<string?> GetModelAsync()
    {
        try
        {
            using var resp = await _probe.GetAsync(_cfg.ModelsUrl);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
            {
                var first = data[0];
                if (first.TryGetProperty("id", out var id))
                    return id.GetString();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Server context window (n_ctx) from llama.cpp /props, or null if unavailable.
    /// This is the hard ceiling for prompt+generation, i.e. the max usable output tokens.</summary>
    public async Task<int?> GetServerMaxContextAsync()
    {
        try
        {
            using var resp = await _probe.GetAsync($"{_cfg.BaseUrl.TrimEnd('/')}/props");
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.TryGetProperty("default_generation_settings", out var dgs) &&
                dgs.TryGetProperty("n_ctx", out var n1) && n1.TryGetInt32(out int v1))
                return v1;
            if (root.TryGetProperty("n_ctx", out var n2) && n2.TryGetInt32(out int v2))
                return v2;
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<string> ChatAsync(string system, string user, int maxTokens, CancellationToken ct)
    {
        var model = string.IsNullOrWhiteSpace(_cfg.Model) ? (await GetModelAsync() ?? "local") : _cfg.Model;

        var payload = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            },
            temperature = _cfg.Temperature,
            max_tokens = maxTokens,          // hard cap — prevents runaway generation
            cache_prompt = true,             // reuse identical system prompt across chunks (faster)
            chat_template_kwargs = new { enable_thinking = false }, // disable Qwen thinking
            stream = false
        };

        var body = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await _chat.PostAsync(_cfg.ChatUrl, body, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {Trim(text, 300)}");

        using var doc = JsonDocument.Parse(text);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
        return content;
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];

    public void Dispose()
    {
        _probe.Dispose();
        _chat.Dispose();
    }
}
