using UnlockGpu.Core.Bench;
using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Model;
using UnlockGpu.Core.Nvml;
using UnlockGpu.Core.Services;

namespace UnlockGpu.Tests;

/// <summary>Assertion helper for the bench/tuner tests (throws on failure).</summary>
internal static class BenchAssert
{
    public static void That(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{what}: expected {expected}, got {actual}");
    }
}

/// <summary>IGpu for tuner tests: only <see cref="Sample"/> (temperature) is meaningful; every setter refuses.</summary>
internal sealed class TunerFakeGpu : IGpu
{
    public uint TempC { get; set; } = 60;
    public int Samples { get; private set; }
    public bool IsAvailable => true;
    public string LastError => "";
    public GpuInfo? Info => null;

    public GpuSample Sample()
    {
        Samples++;
        return new GpuSample { Time = DateTime.Now, TempC = TempC, Ok = true };
    }

    public double EnforcedLimitW() => double.NaN;
    public int SetPowerLimitW(double watts) => throw new InvalidOperationException("tuner must not touch the power limit");
    public OffsetInfo? GetOffset(ClockDomain domain) => throw new InvalidOperationException("tuner must use IOffsetService");
    public int SetOffset(ClockDomain domain, int mhz) => throw new InvalidOperationException("tuner must use IOffsetService");
    public int LockMemoryClock(uint mhz) => throw new InvalidOperationException("tuner must not lock clocks");
    public int ResetMemoryClock() => throw new InvalidOperationException("tuner must not lock clocks");
    public string ErrorText(int rc) => rc.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public void Dispose() { }
}

/// <summary>In-memory offset service with driver ranges, an apply log and optional failures.</summary>
internal sealed class TunerFakeOffsetService(int core = 0, int mem = 0, int coreMax = 1000, int memMax = 1000) : IOffsetService
{
    public int Core { get; private set; } = core;
    public int Mem { get; private set; } = mem;
    public bool Readable { get; set; } = true;
    public Func<int, int, bool> Fails { get; set; } = (_, _) => false;
    public List<(int Core, int Mem)> Applied { get; } = [];

    public OffsetInfo? Get(ClockDomain domain) => !Readable ? null
        : domain == ClockDomain.Core ? new OffsetInfo(Core, -1000, coreMax) : new OffsetInfo(Mem, -2000, memMax);

    public ApplyResult Apply(int coreMHz, int memMHz)
    {
        Applied.Add((coreMHz, memMHz));
        if (Fails(coreMHz, memMHz)) return new ApplyResult(false, $"{coreMHz}/{memMHz}", $"{Core}/{Mem}", "fake failure");
        Core = coreMHz; Mem = memMHz;
        return new ApplyResult(true, $"{coreMHz}/{memMHz}", $"{Core}/{Mem}");
    }
}

/// <summary>Benchmark whose score is a function of the currently applied offset.</summary>
internal sealed class TunerFakeBenchmark(Func<int> currentOffset, Func<int, BenchResult> result) : IGpuBenchmark
{
    public int Calls { get; private set; }
    /// <summary>Runs before the result is produced (offset, call index, token); may throw or wait.</summary>
    public Func<int, int, CancellationToken, Task>? Before { get; set; }

    public async Task<BenchResult> RunAsync(TimeSpan duration, CancellationToken ct)
    {
        int call = Calls++;
        int offset = currentOffset();
        if (Before != null) await Before(offset, call, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return result(offset);
    }

    public static BenchResult Score(double score) => new(score, TimeSpan.FromSeconds(1), true, null);
}

/// <summary>VRAM tester returning an error count as a function of the applied offset.</summary>
internal sealed class TunerFakeVramTester(Func<int> currentOffset, Func<int, long> errors) : IVramTester
{
    public int Calls { get; private set; }
    public List<double> Fractions { get; } = [];

    public Task<VramTestResult> RunAsync(double fractionOfFree, CancellationToken ct)
    {
        Calls++;
        Fractions.Add(fractionOfFree);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new VramTestResult(1L << 30, errors(currentOffset()), true, null));
    }
}

/// <summary>ILog that keeps messages in memory.</summary>
internal sealed class TunerMemoryLog : ILog
{
    public List<string> Lines { get; } = [];
    public void Info(string message) => Lines.Add("I " + message);
    public void Warn(string message) => Lines.Add("W " + message);
    public void Error(string message, Exception? ex = null) => Lines.Add("E " + message + (ex == null ? "" : " " + ex.Message));
}

/// <summary>Synchronous IProgress (System.Progress posts asynchronously, which would make tests racy).</summary>
internal sealed class TunerListProgress : IProgress<TuneStep>
{
    public List<TuneStep> Items { get; } = [];
    public void Report(TuneStep value) => Items.Add(value);
}

/// <summary>Wires the fakes together for one tuner test.</summary>
internal sealed class TunerRig
{
    public TunerFakeGpu Gpu { get; } = new();
    public TunerFakeOffsetService Offsets { get; }
    public TunerFakeBenchmark Bench { get; }
    public TunerFakeVramTester Vram { get; }
    public TunerMemoryLog Log { get; } = new();
    public TuneTarget Target { get; }

    public TunerRig(TuneTarget target, Func<int, double> score, Func<int, long>? vramErrors = null,
        int core = 0, int mem = 0, int max = 1000)
    {
        Target = target;
        Offsets = new TunerFakeOffsetService(core, mem, max, max);
        Bench = new TunerFakeBenchmark(Current, o => TunerFakeBenchmark.Score(score(o)));
        Vram = new TunerFakeVramTester(Current, vramErrors ?? (_ => 0));
    }

    public TunerRig(TuneTarget target, Func<int, BenchResult> bench, int core = 0, int mem = 0, int max = 1000)
    {
        Target = target;
        Offsets = new TunerFakeOffsetService(core, mem, max, max);
        Bench = new TunerFakeBenchmark(Current, bench);
        Vram = new TunerFakeVramTester(Current, _ => 0);
    }

    public int Current() => Target == TuneTarget.Core ? Offsets.Core : Offsets.Mem;

    public AutoTuner Tuner() => new(Gpu, Offsets, Bench, Vram, Log) { TemperaturePollInterval = TimeSpan.FromMilliseconds(5) };

    public TuneResult Run(TuneOptions options, IProgress<TuneStep>? progress = null, CancellationToken ct = default) =>
        Tuner().RunAsync(options with { Target = Target }, progress, ct).GetAwaiter().GetResult();
}
