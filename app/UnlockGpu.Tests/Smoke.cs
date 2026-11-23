using UnlockGpu.Core.Nvml;
using UnlockGpu.Core.Services;

namespace UnlockGpu.Tests;

/// <summary>Read-only check against the real GPU: prints Info and one Sample. Calls nothing that changes the GPU.</summary>
public static class Smoke
{
    public static int Run()
    {
        using var gpu = new NvmlGpu();
        var info = gpu.Info;
        if (info == null)
        {
            Console.WriteLine("NVML unavailable: " + gpu.LastError);
            return 1;
        }
        Console.WriteLine($"Info: {info.Name} | driver {info.Driver} | vbios {info.Vbios}");
        Console.WriteLine($"      power limit min/default/max {info.MinLimitW}/{info.DefaultLimitW}/{info.MaxLimitW} W, max core {info.MaxCoreMHz} MHz");
        Console.WriteLine($"      memory clocks {string.Join(", ", info.MemClocks)}");
        var s = gpu.Sample();
        Console.WriteLine($"Sample: ok={s.Ok} {s.PState} core={s.CoreMHz} sm={s.SmMHz} mem={s.MemMHz} power={s.PowerW:0.0} W limit={s.LimitW:0.0} W " +
            $"temp={s.TempC} C util={s.UtilGpu}/{s.UtilMem} vram={s.VramUsedMB:0}/{s.VramTotalMB:0} MB reasons={s.Reasons} err={s.Error}");

        // System log is read-only as well.
        try
        {
            var ev = new WindowsSystemEventSource().Query(DateTime.UtcNow.AddDays(-7));
            Console.WriteLine($"System log (7 days): {ev.Count} Kernel-Power 41 / WHEA events" + (ev.Count > 0 ? $", newest {ev[0]}" : ""));
        }
        catch (System.ComponentModel.Win32Exception e) { Console.WriteLine("System log query failed: " + e.Message); }
        return s.Ok ? 0 : 1;
    }

    /// <summary>Read-only D3D11 compute benchmark (no clock or power changes); prints the result and NVML samples around it.</summary>
    public static int RunBenchmark(TimeSpan duration)
    {
        using var gpu = new NvmlGpu();
        var log = new MemoryLog();
        var bench = new UnlockGpu.Core.Bench.D3D11Benchmark(log);
        var before = gpu.Sample();
        Console.WriteLine($"Before: {before.PState} core={before.CoreMHz} mem={before.MemMHz} power={before.PowerW:0.0} W temp={before.TempC} C");
        var task = bench.RunAsync(duration, CancellationToken.None);
        Thread.Sleep(duration / 2);
        var mid = gpu.Sample();
        var r = task.GetAwaiter().GetResult();
        Console.WriteLine($"During: {mid.PState} core={mid.CoreMHz} mem={mid.MemMHz} power={mid.PowerW:0.0} W temp={mid.TempC} C util={mid.UtilGpu}% vram={mid.VramUsedMB:0}/{mid.VramTotalMB:0} MB reasons={mid.Reasons}");
        Console.WriteLine($"Bench:  score={r.Score:0.0} Melem/s duration={r.Duration.TotalSeconds:0.00} s completed={r.Completed} error={r.ErrorKey}");
        foreach (string line in log.Lines) Console.WriteLine("  log: " + line);
        return r.Completed ? 0 : 1;
    }
}
