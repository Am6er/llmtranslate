using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlmTranslate;

/// <summary>Config loaded from config.json next to the exe.</summary>
public class AppConfig
{
    /// <summary>OpenAI-compatible base URL of the local llama.cpp server (e.g. Qwen3.5-9B on :8080).</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8080";

    /// <summary>Model id to send. Empty = auto-detect from /v1/models.</summary>
    public string Model { get; set; } = "";

    public string SourceLang { get; set; } = "English";
    public string TargetLang { get; set; } = "Russian";

    public double Temperature { get; set; } = 0.3;
    public int MaxCharsPerChunk { get; set; } = 1800;
    public int MaxTokens { get; set; } = 4096;       // per-chunk output cap (anti-runaway)
    public int RequestTimeoutSec { get; set; } = 180;
    public string OutputSuffix { get; set; } = "_RU";

    /// <summary>Retries per chunk on transient network/server errors before giving up.</summary>
    public int RetryCount { get; set; } = 4;

    /// <summary>Target max GPU temperature (°C). After each chunk the GPU temp is read;
    /// if it is above this target the app pauses (cools down) before the next chunk.
    /// Set high (e.g. 90) to effectively disable throttling.</summary>
    public int TargetTempC { get; set; } = 75;

    /// <summary>Path/command for nvidia-smi (used to read GPU temperature).</summary>
    public string NvidiaSmiPath { get; set; } = "nvidia-smi";

    [JsonIgnore] public string ModelsUrl => $"{BaseUrl.TrimEnd('/')}/v1/models";
    [JsonIgnore] public string ChatUrl => $"{BaseUrl.TrimEnd('/')}/v1/chat/completions";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOpts);
                if (cfg != null) return cfg;
            }
        }
        catch { /* defaults */ }

        var def = new AppConfig();
        try { def.Save(); } catch { }
        return def;
    }

    public void Save() => File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
}
