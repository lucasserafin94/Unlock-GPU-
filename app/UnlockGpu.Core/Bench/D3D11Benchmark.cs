using System.Diagnostics;
using System.Globalization;
using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Services;

namespace UnlockGpu.Core.Bench;

/// <summary>D3D11 compute benchmark on the NVIDIA adapter (raw COM, no extra packages).
/// <para>Score = million buffer elements processed per second of GPU time, where one element is one 4-byte read,
/// 128 FMAs and one 4-byte write. Time comes from GPU timestamp queries (TIMESTAMP_DISJOINT), so CPU scheduling
/// cannot skew it; if the timestamps are unusable (clock domain changed) it falls back to wall time between
/// submission and completion of the same batches. The first ~15 % of the duration (at most 1 s) is a warm-up and is not scored.</para>
/// <para>Read/compute load only: nothing here changes clocks, power limits or driver settings.</para></summary>
public sealed class D3D11Benchmark : IGpuBenchmark
{
    const uint Elements = 64u * 1024 * 1024;     // 256 MB of floats: larger than the L2 cache
    const uint FmaIterations = 32;               // x 4 accumulators = 128 FMAs per element
    const int MaxInFlight = 3;
    const double TargetBatchSeconds = 0.02;
    const int MaxDispatchesPerBatch = 2000;

    readonly ILog _log;

    public D3D11Benchmark(ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <inheritdoc />
    /// <remarks>Throws <see cref="OperationCanceledException"/> on cancellation. A removed/hung device returns
    /// <c>Completed = false</c> with <see cref="BenchErrorKeys.DeviceRemoved"/>.</remarks>
    public Task<BenchResult> RunAsync(TimeSpan duration, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        // D3D11 immediate contexts are single-threaded: everything runs on one dedicated thread.
        return Task.Factory.StartNew(() => Run(duration, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    BenchResult Run(TimeSpan duration, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        try
        {
            using var gpu = D3DContext.Create(_log);
            var result = Measure(gpu, duration, ct);
            _log.Info(string.Create(CultureInfo.InvariantCulture,
                $"Benchmark: score {result.Score:F1} Melem/s over {result.Duration.TotalSeconds:F1} s, completed={result.Completed}"));
            return result;
        }
        catch (GpuComputeException e)
        {
            _log.Warn("Benchmark failed: " + e.Message);
            return new BenchResult(0, total.Elapsed, false, e.ErrorKey);
        }
    }

    sealed class Batch(D3DContext gpu) : IDisposable
    {
        public ComObject Disjoint { get; } = gpu.CreateQuery(D3DContext.QueryTimestampDisjoint);
        public ComObject Start { get; } = gpu.CreateQuery(D3DContext.QueryTimestamp);
        public ComObject End { get; } = gpu.CreateQuery(D3DContext.QueryTimestamp);
        public int Dispatches { get; set; }
        public TimeSpan Submitted { get; set; }
        public bool Scored { get; set; }

        public void Dispose() { Disjoint.Dispose(); Start.Dispose(); End.Dispose(); }
    }

    unsafe BenchResult Measure(D3DContext gpu, TimeSpan duration, CancellationToken ct)
    {
        if (gpu.QueryLocalMemory() is { } mem)
            _log.Info(string.Create(CultureInfo.InvariantCulture,
                $"Benchmark: local VRAM budget {mem.Budget >> 20} MB, used {mem.CurrentUsage >> 20} MB, available for reservation {mem.AvailableForReservation >> 20} MB"));
        byte[] code = ShaderCompiler.Compile(ComputeShaders.Bench, "Bench");
        using var shader = gpu.CreateComputeShader(code);
        using var data = gpu.CreateBuffer(BufferDesc.Structured(Elements * 4));
        using var uav = gpu.CreateUnorderedAccessView(data);
        using var constants = gpu.CreateBuffer(BufferDesc.Constants(16));
        gpu.BindCompute(shader, constants);
        gpu.BindUav(uav);
        gpu.UpdateConstants(constants, new ShaderParams(Elements, ComputeShaders.Threads, FmaIterations, 1));
        gpu.Dispatch(ComputeShaders.Groups);
        gpu.UpdateConstants(constants, new ShaderParams(Elements, ComputeShaders.Threads, FmaIterations, 0));

        var pool = new List<Batch>();
        try
        {
            for (int i = 0; i < MaxInFlight; i++) pool.Add(new Batch(gpu));
            var free = new Stack<Batch>(pool);
            var inFlight = new Queue<Batch>();
            var clock = Stopwatch.StartNew();
            var warmup = TimeSpan.FromTicks(Math.Min(duration.Ticks * 15 / 100, TimeSpan.TicksPerSecond));
            var end = duration; // the warm-up is part of the requested duration

            int perBatch = 4;
            long scoredDispatches = 0, timedDispatches = 0;
            double gpuSeconds = 0;
            TimeSpan? firstScoredSubmit = null, lastScoredDone = null;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                while (clock.Elapsed < end && free.Count > 0)
                {
                    var b = free.Pop();
                    b.Dispatches = perBatch;
                    b.Submitted = clock.Elapsed;
                    b.Scored = b.Submitted >= warmup;
                    gpu.Begin(b.Disjoint);
                    gpu.End(b.Start);
                    for (int d = 0; d < perBatch; d++) gpu.Dispatch(ComputeShaders.Groups);
                    gpu.End(b.End);
                    gpu.End(b.Disjoint);
                    gpu.Flush();
                    inFlight.Enqueue(b);
                }
                if (inFlight.Count == 0) break;

                var oldest = inFlight.Peek();
                TimestampDisjoint dj;
                int hr = gpu.GetData(oldest.Disjoint, &dj, (uint)sizeof(TimestampDisjoint));
                if (hr == D3DNative.SFalse) { Thread.Sleep(1); continue; }
                gpu.Check(hr, "GetData(disjoint)");
                inFlight.Dequeue();

                ulong t0 = 0, t1 = 0;
                gpu.Check(WaitData(gpu, oldest.Start, &t0, ct), "GetData(timestamp)");
                gpu.Check(WaitData(gpu, oldest.End, &t1, ct), "GetData(timestamp)");
                bool timed = dj.Disjoint == 0 && dj.Frequency > 0 && t1 > t0;
                if (timed)
                {
                    double perDispatch = (t1 - t0) / (double)dj.Frequency / oldest.Dispatches;
                    perBatch = Math.Clamp((int)Math.Round(TargetBatchSeconds / Math.Max(perDispatch, 1e-7)), 1,
                        Math.Min(MaxDispatchesPerBatch, Math.Max(perBatch * 4, 4)));
                }
                if (oldest.Scored)
                {
                    scoredDispatches += oldest.Dispatches;
                    firstScoredSubmit ??= oldest.Submitted;
                    lastScoredDone = clock.Elapsed;
                    if (timed)
                    {
                        gpuSeconds += (t1 - t0) / (double)dj.Frequency;
                        timedDispatches += oldest.Dispatches;
                    }
                }
                free.Push(oldest);
            }

            gpu.Check(gpu.DeviceRemovedReason(), "DeviceRemovedReason");
            if (gpu.QueryLocalMemory() is { } after)
                _log.Info(string.Create(CultureInfo.InvariantCulture,
                    $"Benchmark: after run, local VRAM budget {after.Budget >> 20} MB, used {after.CurrentUsage >> 20} MB"));
            var measured = clock.Elapsed - warmup;
            if (scoredDispatches == 0) return new BenchResult(0, measured, false, BenchErrorKeys.TooShort);

            double score;
            if (timedDispatches * 2 >= scoredDispatches && gpuSeconds > 0)
                score = timedDispatches * (double)Elements / gpuSeconds / 1e6;
            else
            {
                double wall = (lastScoredDone!.Value - firstScoredSubmit!.Value).TotalSeconds;
                _log.Warn("Benchmark: GPU timestamps unusable (disjoint); using wall time.");
                score = wall > 0 ? scoredDispatches * (double)Elements / wall / 1e6 : 0;
            }
            return new BenchResult(score, measured, true, null);
        }
        finally
        {
            foreach (var b in pool) b.Dispose();
        }
    }

    static unsafe int WaitData(D3DContext gpu, ComObject query, ulong* value, CancellationToken ct)
    {
        while (true)
        {
            int hr = gpu.GetData(query, value, sizeof(ulong));
            if (hr != D3DNative.SFalse) return hr;
            ct.ThrowIfCancellationRequested();
            Thread.Sleep(0);
        }
    }
}
