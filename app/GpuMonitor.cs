using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LlmTranslate;

/// <summary>Reads the NVIDIA GPU temperature via nvidia-smi.</summary>
public sealed class GpuMonitor
{
    private readonly string _exe;

    public GpuMonitor(string nvidiaSmiPath)
    {
        _exe = string.IsNullOrWhiteSpace(nvidiaSmiPath) ? "nvidia-smi" : nvidiaSmiPath;
    }

    /// <summary>Current GPU temperature in °C, or null if nvidia-smi is unavailable.</summary>
    public async Task<int?> ReadTempAsync(CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo(_exe,
                "--query-gpu=temperature.gpu --format=csv,noheader,nounits")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return null;

            string outp = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);

            string? line = outp.Split('\n').Select(s => s.Trim()).FirstOrDefault(s => s.Length > 0);
            return int.TryParse(line, out int t) ? t : (int?)null;
        }
        catch
        {
            return null;
        }
    }
}
