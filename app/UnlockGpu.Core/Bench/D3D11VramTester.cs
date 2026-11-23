using System.Globalization;
using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Services;

namespace UnlockGpu.Core.Bench;

/// <summary>VRAM pattern test over D3D11 (raw COM, no extra packages).
/// <para>Allocates buffers totalling a fraction of the free local VRAM (IDXGIAdapter3::QueryVideoMemoryInfo; see
/// <see cref="VramPlan"/>), fills them on the GPU with each of <see cref="VramPatterns"/>' patterns, copies them back
/// through a staging buffer chunk by chunk and counts mismatching 32-bit words on the CPU. Everything is released
/// at the end. Read/write of its own buffers only: no clock, power or driver setting is touched.</para></summary>
public sealed class D3D11VramTester : IVramTester
{
    /// <summary>Size of each test buffer; 128 MB is within the D3D11 per-resource limit on every GPU.</summary>
    public const long ChunkBytes = 128L << 20;
    const long MinimumBytes = 16L << 20;

    readonly ILog _log;

    public D3D11VramTester(ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <inheritdoc />
    /// <remarks>Throws <see cref="OperationCanceledException"/> on cancellation (all buffers are still released).</remarks>
    public Task<VramTestResult> RunAsync(double fractionOfFree, CancellationToken ct)
    {
        if (double.IsNaN(fractionOfFree) || fractionOfFree <= 0 || fractionOfFree > 1)
            throw new ArgumentOutOfRangeException(nameof(fractionOfFree), fractionOfFree, "Expected a value in (0, 1].");
        return Task.Factory.StartNew(() => Run(fractionOfFree, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    VramTestResult Run(double fraction, CancellationToken ct)
    {
        try
        {
            using var gpu = D3DContext.Create(_log);
            return Test(gpu, fraction, ct);
        }
        catch (GpuComputeException e)
        {
            _log.Warn("VRAM test failed: " + e.Message);
            return new VramTestResult(0, 0, false, e.ErrorKey);
        }
    }

    sealed class Chunk(ComObject buffer, ComObject view, long bytes, ulong firstWord) : IDisposable
    {
        public ComObject Buffer { get; } = buffer;
        public ComObject View { get; } = view;
        public long Bytes { get; } = bytes;
        public ulong FirstWord { get; } = firstWord;
        public uint Words => (uint)(Bytes / 4);
        public void Dispose() { View.Dispose(); Buffer.Dispose(); }
    }

    unsafe VramTestResult Test(D3DContext gpu, double fraction, CancellationToken ct)
    {
        if (gpu.QueryLocalMemory() is not { } memory) return new VramTestResult(0, 0, false, BenchErrorKeys.VramNoInfo);
        long free = memory.Budget > memory.CurrentUsage ? (long)(memory.Budget - memory.CurrentUsage) : 0;
        long target = VramPlan.BytesToTest(free, fraction);
        _log.Info(string.Create(CultureInfo.InvariantCulture,
            $"VRAM test: budget {memory.Budget >> 20} MB, used {memory.CurrentUsage >> 20} MB, testing {target >> 20} MB"));
        if (target < MinimumBytes) return new VramTestResult(0, 0, false, BenchErrorKeys.VramNotEnoughFree);

        byte[] code = ShaderCompiler.Compile(ComputeShaders.Fill, "Fill");
        var chunks = new List<Chunk>();
        try
        {
            long allocated = 0;
            while (allocated < target)
            {
                ct.ThrowIfCancellationRequested();
                long size = Math.Min(ChunkBytes, target - allocated);
                int hr = gpu.TryCreateBuffer(BufferDesc.Structured((uint)size), out var buffer);
                if (hr == D3DNative.EOutOfMemory)
                {
                    _log.Warn(string.Create(CultureInfo.InvariantCulture, $"VRAM test: allocation stopped at {allocated >> 20} MB (out of memory)"));
                    break;
                }
                gpu.Check(hr, "CreateBuffer(vram)");
                try { chunks.Add(new Chunk(buffer!, gpu.CreateUnorderedAccessView(buffer!), size, (ulong)allocated / 4)); }
                catch { buffer!.Dispose(); throw; }
                allocated += size;
            }
            if (chunks.Count == 0) return new VramTestResult(0, 0, false, BenchErrorKeys.VramAllocFailed);

            using var shader = gpu.CreateComputeShader(code);
            using var constants = gpu.CreateBuffer(BufferDesc.Constants(16));
            using var staging = gpu.CreateBuffer(BufferDesc.StagingRead((uint)Math.Min(ChunkBytes, allocated)));
            gpu.BindCompute(shader, constants);

            long errors = 0;
            for (uint pattern = 0; pattern < VramPatterns.Count; pattern++)
            {
                foreach (var c in chunks)
                {
                    ct.ThrowIfCancellationRequested();
                    gpu.BindUav(c.View);
                    gpu.UpdateConstants(constants, new ShaderParams(c.Words, ComputeShaders.Threads, unchecked((uint)c.FirstWord), pattern));
                    gpu.Dispatch(ComputeShaders.Groups);
                }
                gpu.Flush();
                foreach (var c in chunks)
                {
                    ct.ThrowIfCancellationRequested();
                    gpu.CopyBufferRegion(staging, c.Buffer, (uint)c.Bytes);
                    void* mapped = gpu.MapRead(staging);
                    try { errors += VramPatterns.CountMismatches(new ReadOnlySpan<uint>(mapped, (int)c.Words), unchecked((uint)c.FirstWord), pattern); }
                    finally { gpu.Unmap(staging); }
                }
            }
            gpu.Check(gpu.DeviceRemovedReason(), "DeviceRemovedReason");
            _log.Info(string.Create(CultureInfo.InvariantCulture, $"VRAM test: {allocated >> 20} MB x {VramPatterns.Count} patterns, {errors} bad words"));
            return new VramTestResult(allocated, errors, true, null);
        }
        finally
        {
            foreach (var c in chunks) c.Dispose();
        }
    }
}
