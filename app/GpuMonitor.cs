using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LlmTranslate;

/// <summary>Reads NVIDIA GPU temperature / utilization and compute-process PIDs via nvidia-smi.</summary>
public sealed class GpuMonitor
{
    private readonly string _exe;

    public GpuMonitor(string nvidiaSmiPath)
    {
        _exe = string.IsNullOrWhiteSpace(nvidiaSmiPath) ? "nvidia-smi" : nvidiaSmiPath;
    }

    /// <summary>Current GPU temperature in °C, or null if nvidia-smi is unavailable.</summary>
    public async Task<int?> ReadTempAsync(CancellationToken ct = default)
        => (await ReadTempUtilAsync(ct)).temp;

    /// <summary>Current GPU temperature (°C) and utilization (%), either null if unavailable.
    /// Utilization lets the throttle avoid pausing during idle/CPU-bound phases.</summary>
    public async Task<(int? temp, int? util)> ReadTempUtilAsync(CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo(_exe,
                "--query-gpu=temperature.gpu,utilization.gpu --format=csv,noheader,nounits")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return (null, null);

            string outp = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);

            string? line = outp.Split('\n').Select(s => s.Trim()).FirstOrDefault(s => s.Length > 0);
            if (line == null) return (null, null);
            var parts = line.Split(',');
            int? temp = parts.Length > 0 && int.TryParse(parts[0].Trim(), out int t) ? t : (int?)null;
            int? util = parts.Length > 1 && int.TryParse(parts[1].Trim(), out int u) ? u : (int?)null;
            return (temp, util);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>PIDs of processes with an active CUDA context on the GPU (the actual compute
    /// workers — here the llama.cpp server). The throttle suspends only these.</summary>
    public async Task<List<int>> ReadComputePidsAsync(CancellationToken ct = default)
    {
        var pids = new List<int>();
        try
        {
            var psi = new ProcessStartInfo(_exe,
                "--query-compute-apps=pid --format=csv,noheader,nounits")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return pids;

            string outp = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);

            foreach (var line in outp.Split('\n'))
                if (int.TryParse(line.Trim(), out int pid) && pid > 0)
                    pids.Add(pid);
        }
        catch { /* nvidia-smi unavailable */ }
        return pids;
    }
}
