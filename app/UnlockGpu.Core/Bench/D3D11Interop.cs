using System.Runtime.InteropServices;
using System.Text;
using UnlockGpu.Core.Infrastructure;

namespace UnlockGpu.Core.Bench;

/// <summary>Error raised by the D3D11 compute helpers. <see cref="ErrorKey"/> is a Localizer key for the UI.</summary>
public sealed class GpuComputeException : Exception
{
    /// <summary>HRESULT that caused the failure (0 when not applicable).</summary>
    public int HResultCode { get; }

    /// <summary>Localizer key describing the failure (for example <see cref="BenchErrorKeys.DeviceRemoved"/>).</summary>
    public string ErrorKey { get; } = BenchErrorKeys.Failed;

    public GpuComputeException() { }
    public GpuComputeException(string message) : base(message) { }
    public GpuComputeException(string message, Exception inner) : base(message, inner) { }

    public GpuComputeException(string errorKey, int hr, string message) : base(message)
    {
        ErrorKey = errorKey;
        HResultCode = hr;
    }
}

/// <summary>Localizer keys reported in <c>BenchResult.ErrorKey</c> / <c>VramTestResult.ErrorKey</c>.</summary>
public static class BenchErrorKeys
{
    public const string DeviceRemoved = "bench.deviceRemoved";
    public const string NoNvidiaAdapter = "bench.noNvidiaAdapter";
    public const string DeviceCreateFailed = "bench.deviceCreateFailed";
    public const string ShaderFailed = "bench.shaderFailed";
    public const string TooShort = "bench.tooShort";
    public const string Failed = "bench.failed";
    public const string VramNoInfo = "vram.noMemoryInfo";
    public const string VramNotEnoughFree = "vram.notEnoughFree";
    public const string VramAllocFailed = "vram.allocFailed";
}

/// <summary>Flat exports of the system DLLs used by the compute helpers. All three ship with Windows.</summary>
internal static unsafe class D3DNative
{
    [DllImport("dxgi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int CreateDXGIFactory1(Guid* riid, IntPtr* factory);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, int* featureLevels,
        uint featureLevelCount, uint sdkVersion, IntPtr* device, int* featureLevel, IntPtr* context);

    [DllImport("d3dcompiler_47.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int D3DCompile(byte* source, nuint sourceSize, byte* sourceName, IntPtr defines, IntPtr include,
        byte* entryPoint, byte* target, uint flags1, uint flags2, IntPtr* code, IntPtr* errors);

    public const int SOk = 0;
    public const int SFalse = 1;
    public const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    public const int DxgiErrorDeviceRemoved = unchecked((int)0x887A0005);
    public const int DxgiErrorDeviceHung = unchecked((int)0x887A0006);
    public const int DxgiErrorDeviceReset = unchecked((int)0x887A0007);
    public const int DxgiErrorDriverInternal = unchecked((int)0x887A0020);
    public const int EOutOfMemory = unchecked((int)0x8007000E);

    public static bool IsDeviceLost(int hr) =>
        hr is DxgiErrorDeviceRemoved or DxgiErrorDeviceHung or DxgiErrorDeviceReset or DxgiErrorDriverInternal;

    public static readonly Guid IidDxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    public static readonly Guid IidDxgiAdapter3 = new("645967A4-1392-4310-A798-8053CE3E93FD");
}

/// <summary>DXGI_ADAPTER_DESC1.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AdapterDesc1
{
    public fixed char Description[128];
    public uint VendorId;
    public uint DeviceId;
    public uint SubSysId;
    public uint Revision;
    public nuint DedicatedVideoMemory;
    public nuint DedicatedSystemMemory;
    public nuint SharedSystemMemory;
    public long AdapterLuid;
    public uint Flags;
}

/// <summary>D3D11_BUFFER_DESC.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BufferDesc
{
    public uint ByteWidth;
    public int Usage;
    public uint BindFlags;
    public uint CpuAccessFlags;
    public uint MiscFlags;
    public uint StructureByteStride;

    public const int UsageDefault = 0;
    public const int UsageStaging = 3;
    public const uint BindConstantBuffer = 0x4;
    public const uint BindUnorderedAccess = 0x80;
    public const uint CpuRead = 0x20000;
    public const uint MiscStructured = 0x40;

    public static BufferDesc Structured(uint bytes) => new()
    {
        ByteWidth = bytes, Usage = UsageDefault, BindFlags = BindUnorderedAccess, MiscFlags = MiscStructured, StructureByteStride = 4,
    };

    public static BufferDesc Constants(uint bytes) => new() { ByteWidth = bytes, Usage = UsageDefault, BindFlags = BindConstantBuffer };

    public static BufferDesc StagingRead(uint bytes) => new() { ByteWidth = bytes, Usage = UsageStaging, CpuAccessFlags = CpuRead };
}

/// <summary>DXGI_QUERY_VIDEO_MEMORY_INFO.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct VideoMemoryInfo
{
    public ulong Budget;
    public ulong CurrentUsage;
    public ulong AvailableForReservation;
    public ulong CurrentReservation;
}

/// <summary>D3D11_QUERY_DATA_TIMESTAMP_DISJOINT.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TimestampDisjoint
{
    public ulong Frequency;
    public int Disjoint;
    public int Padding;
}

/// <summary>The 16-byte constant buffer shared by both compute shaders.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ShaderParams(uint a, uint b, uint c, uint d)
{
    public uint A = a;
    public uint B = b;
    public uint C = c;
    public uint D = d;
}

/// <summary>Owns one COM reference and releases it exactly once. Vtable slots are called through function pointers.</summary>
internal sealed unsafe class ComObject : IDisposable
{
    IntPtr _ptr;

    public ComObject(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) throw new ArgumentException("Null COM pointer.", nameof(ptr));
        _ptr = ptr;
    }

    public IntPtr Ptr => _ptr != IntPtr.Zero ? _ptr : throw new ObjectDisposedException(nameof(ComObject));

    /// <summary>Address of vtable entry <paramref name="index"/>.</summary>
    public void* Slot(int index) => (*(void***)Ptr)[index];

    public ComObject? QueryInterface(Guid iid)
    {
        IntPtr result;
        int hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Slot(0))(Ptr, &iid, &result);
        return hr >= 0 && result != IntPtr.Zero ? new ComObject(result) : null;
    }

    public void Dispose()
    {
        IntPtr p = Interlocked.Exchange(ref _ptr, IntPtr.Zero);
        if (p != IntPtr.Zero) _ = ((delegate* unmanaged<IntPtr, uint>)(*(void***)p)[2])(p);
    }
}

/// <summary>Compiles HLSL with d3dcompiler_47.dll.</summary>
internal static unsafe class ShaderCompiler
{
    const uint OptimizationLevel3 = 1u << 15;

    public static byte[] Compile(string source, string entryPoint, string target = "cs_5_0")
    {
        byte[] src = Encoding.ASCII.GetBytes(source);
        byte[] name = Encoding.ASCII.GetBytes("UnlockGpuBench\0");
        byte[] entry = Encoding.ASCII.GetBytes(entryPoint + "\0");
        byte[] tgt = Encoding.ASCII.GetBytes(target + "\0");
        IntPtr code = IntPtr.Zero, errors = IntPtr.Zero;
        int hr;
        try
        {
            fixed (byte* pSrc = src, pName = name, pEntry = entry, pTarget = tgt)
                hr = D3DNative.D3DCompile(pSrc, (nuint)src.Length, pName, IntPtr.Zero, IntPtr.Zero, pEntry, pTarget,
                    OptimizationLevel3, 0, &code, &errors);
        }
        catch (DllNotFoundException e)
        {
            throw new GpuComputeException(BenchErrorKeys.ShaderFailed, 0, "d3dcompiler_47.dll: " + e.Message);
        }

        using var codeBlob = code != IntPtr.Zero ? new ComObject(code) : null;
        using var errorBlob = errors != IntPtr.Zero ? new ComObject(errors) : null;
        if (hr < 0 || codeBlob == null)
        {
            string detail = errorBlob != null ? Marshal.PtrToStringAnsi(BlobPointer(errorBlob), (int)BlobSize(errorBlob)) : "";
            throw new GpuComputeException(BenchErrorKeys.ShaderFailed, hr, $"D3DCompile 0x{hr:X8}: {detail}");
        }
        var bytes = new byte[(int)BlobSize(codeBlob)];
        Marshal.Copy(BlobPointer(codeBlob), bytes, 0, bytes.Length);
        return bytes;
    }

    static IntPtr BlobPointer(ComObject blob) => ((delegate* unmanaged<IntPtr, IntPtr>)blob.Slot(3))(blob.Ptr);
    static nuint BlobSize(ComObject blob) => ((delegate* unmanaged<IntPtr, nuint>)blob.Slot(4))(blob.Ptr);
}

/// <summary>A D3D11 hardware device on the NVIDIA adapter plus its immediate context. Not thread-safe: use from one thread.
/// Only compute work is ever submitted; nothing here changes clocks, power or any driver setting.</summary>
internal sealed unsafe class D3DContext : IDisposable
{
    const uint VendorNvidia = 0x10DE;
    const uint AdapterFlagSoftware = 0x2;
    const int FeatureLevel11_0 = 0xB000;
    const uint SdkVersion = 7;

    readonly ComObject _adapter;
    readonly ComObject? _adapter3;
    readonly ComObject _device;
    readonly ComObject _context;

    public string AdapterName { get; }

    D3DContext(ComObject adapter, ComObject? adapter3, ComObject device, ComObject context, string name)
    {
        _adapter = adapter; _adapter3 = adapter3; _device = device; _context = context; AdapterName = name;
    }

    /// <summary>Creates a hardware device on the NVIDIA adapter with the most dedicated memory.</summary>
    public static D3DContext Create(ILog log)
    {
        IntPtr factoryPtr;
        Guid iid = D3DNative.IidDxgiFactory1;
        int hr;
        try { hr = D3DNative.CreateDXGIFactory1(&iid, &factoryPtr); }
        catch (DllNotFoundException e) { throw new GpuComputeException(BenchErrorKeys.DeviceCreateFailed, 0, "dxgi.dll: " + e.Message); }
        if (hr < 0) throw new GpuComputeException(BenchErrorKeys.DeviceCreateFailed, hr, $"CreateDXGIFactory1 0x{hr:X8}");

        using var factory = new ComObject(factoryPtr);
        ComObject? best = null;
        string bestName = "";
        ulong bestMemory = 0;
        for (uint i = 0; ; i++)
        {
            IntPtr adapterPtr;
            hr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)factory.Slot(12))(factory.Ptr, i, &adapterPtr); // EnumAdapters1
            if (hr == D3DNative.DxgiErrorNotFound || hr < 0) break;
            var adapter = new ComObject(adapterPtr);
            AdapterDesc1 desc;
            hr = ((delegate* unmanaged<IntPtr, AdapterDesc1*, int>)adapter.Slot(10))(adapter.Ptr, &desc); // GetDesc1
            bool nvidia = hr >= 0 && desc.VendorId == VendorNvidia && (desc.Flags & AdapterFlagSoftware) == 0;
            if (nvidia && (best == null || desc.DedicatedVideoMemory > bestMemory))
            {
                best?.Dispose();
                best = adapter;
                bestMemory = desc.DedicatedVideoMemory;
                bestName = new string(desc.Description); // null-terminated WCHAR[128]
            }
            else adapter.Dispose();
        }
        if (best == null) throw new GpuComputeException(BenchErrorKeys.NoNvidiaAdapter, 0, "No NVIDIA DXGI adapter found.");

        try
        {
            int level = FeatureLevel11_0, created;
            IntPtr devicePtr, contextPtr;
            hr = D3DNative.D3D11CreateDevice(best.Ptr, 0 /* UNKNOWN: adapter given */, IntPtr.Zero, 0, &level, 1, SdkVersion,
                &devicePtr, &created, &contextPtr);
            if (hr < 0) throw new GpuComputeException(BenchErrorKeys.DeviceCreateFailed, hr, $"D3D11CreateDevice 0x{hr:X8}");
            var adapter3 = best.QueryInterface(D3DNative.IidDxgiAdapter3);
            log.Info($"D3D11 compute device on '{bestName}' ({bestMemory / 1048576} MB dedicated)");
            return new D3DContext(best, adapter3, new ComObject(devicePtr), new ComObject(contextPtr), bestName);
        }
        catch
        {
            best.Dispose();
            throw;
        }
    }

    /// <summary>Local (dedicated) memory budget and usage of this process' view of the adapter, or null if unavailable.</summary>
    public VideoMemoryInfo? QueryLocalMemory()
    {
        if (_adapter3 == null) return null;
        VideoMemoryInfo info;
        int hr = ((delegate* unmanaged<IntPtr, uint, int, VideoMemoryInfo*, int>)_adapter3.Slot(14))(_adapter3.Ptr, 0, 0, &info);
        return hr >= 0 ? info : null;
    }

    // ---------------- ID3D11Device ----------------

    public int TryCreateBuffer(BufferDesc desc, out ComObject? buffer)
    {
        IntPtr p;
        int hr = ((delegate* unmanaged<IntPtr, BufferDesc*, IntPtr, IntPtr*, int>)_device.Slot(3))(_device.Ptr, &desc, IntPtr.Zero, &p);
        buffer = hr >= 0 ? new ComObject(p) : null;
        return hr;
    }

    public ComObject CreateBuffer(BufferDesc desc)
    {
        int hr = TryCreateBuffer(desc, out var buffer);
        Check(hr, "CreateBuffer");
        return buffer!;
    }

    public ComObject CreateUnorderedAccessView(ComObject resource)
    {
        IntPtr p;
        int hr = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr*, int>)_device.Slot(8))(_device.Ptr, resource.Ptr, IntPtr.Zero, &p);
        Check(hr, "CreateUnorderedAccessView");
        return new ComObject(p);
    }

    public ComObject CreateComputeShader(byte[] bytecode)
    {
        IntPtr p;
        int hr;
        fixed (byte* code = bytecode)
            hr = ((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)_device.Slot(18))(_device.Ptr, code, (nuint)bytecode.Length, IntPtr.Zero, &p);
        Check(hr, "CreateComputeShader");
        return new ComObject(p);
    }

    public const int QueryTimestamp = 2;
    public const int QueryTimestampDisjoint = 3;

    public ComObject CreateQuery(int type)
    {
        IntPtr p;
        var desc = stackalloc int[2];
        desc[0] = type; desc[1] = 0;
        int hr = ((delegate* unmanaged<IntPtr, int*, IntPtr*, int>)_device.Slot(24))(_device.Ptr, desc, &p);
        Check(hr, "CreateQuery");
        return new ComObject(p);
    }

    /// <summary>S_OK while the device is healthy; a DXGI_ERROR_DEVICE_* code once it was removed or reset.</summary>
    public int DeviceRemovedReason() => ((delegate* unmanaged<IntPtr, int>)_device.Slot(39))(_device.Ptr);

    // ---------------- ID3D11DeviceContext ----------------

    public void BindCompute(ComObject shader, ComObject constants)
    {
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)_context.Slot(69))(_context.Ptr, shader.Ptr, null, 0);
        IntPtr cb = constants.Ptr;
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)_context.Slot(71))(_context.Ptr, 0, 1, &cb);
    }

    public void BindUav(ComObject uav)
    {
        IntPtr u = uav.Ptr;
        uint initial = uint.MaxValue;
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, uint*, void>)_context.Slot(68))(_context.Ptr, 0, 1, &u, &initial);
    }

    public void UpdateConstants(ComObject buffer, ShaderParams value) =>
        ((delegate* unmanaged<IntPtr, IntPtr, uint, void*, void*, uint, uint, void>)_context.Slot(48))(_context.Ptr, buffer.Ptr, 0, null, &value, 0, 0);

    public void Dispatch(uint groupsX) =>
        ((delegate* unmanaged<IntPtr, uint, uint, uint, void>)_context.Slot(41))(_context.Ptr, groupsX, 1, 1);

    public void Begin(ComObject query) => ((delegate* unmanaged<IntPtr, IntPtr, void>)_context.Slot(27))(_context.Ptr, query.Ptr);

    public void End(ComObject query) => ((delegate* unmanaged<IntPtr, IntPtr, void>)_context.Slot(28))(_context.Ptr, query.Ptr);

    /// <summary>S_OK when ready, S_FALSE while pending, negative on failure (device removed).</summary>
    public int GetData(ComObject query, void* data, uint size) =>
        ((delegate* unmanaged<IntPtr, IntPtr, void*, uint, uint, int>)_context.Slot(29))(_context.Ptr, query.Ptr, data, size, 0);

    /// <summary>Copies the first <paramref name="bytes"/> of <paramref name="source"/> into the start of <paramref name="destination"/>.</summary>
    public void CopyBufferRegion(ComObject destination, ComObject source, uint bytes)
    {
        var box = stackalloc uint[6];
        box[0] = 0; box[1] = 0; box[2] = 0; box[3] = bytes; box[4] = 1; box[5] = 1; // left, top, front, right, bottom, back
        ((delegate* unmanaged<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, uint*, void>)_context.Slot(46))(
            _context.Ptr, destination.Ptr, 0, 0, 0, 0, source.Ptr, 0, box);
    }

    /// <summary>Maps a staging buffer for reading (blocks until the GPU finished writing it).</summary>
    public void* MapRead(ComObject staging)
    {
        var mapped = stackalloc IntPtr[2]; // D3D11_MAPPED_SUBRESOURCE: pData, RowPitch, DepthPitch
        int hr = ((delegate* unmanaged<IntPtr, IntPtr, uint, int, uint, IntPtr*, int>)_context.Slot(14))(_context.Ptr, staging.Ptr, 0, 1 /* READ */, 0, mapped);
        Check(hr, "Map");
        return (void*)mapped[0];
    }

    public void Unmap(ComObject staging) => ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)_context.Slot(15))(_context.Ptr, staging.Ptr, 0);

    public void Flush() => ((delegate* unmanaged<IntPtr, void>)_context.Slot(111))(_context.Ptr);

    void ClearState() => ((delegate* unmanaged<IntPtr, void>)_context.Slot(110))(_context.Ptr);

    /// <summary>Throws a <see cref="GpuComputeException"/> for a failed HRESULT, classifying device loss.</summary>
    public void Check(int hr, string what)
    {
        if (hr >= 0) return;
        bool lost = D3DNative.IsDeviceLost(hr) || DeviceRemovedReason() != D3DNative.SOk;
        if (lost) throw new GpuComputeException(BenchErrorKeys.DeviceRemoved, hr, $"{what}: device removed 0x{hr:X8} (reason 0x{DeviceRemovedReason():X8})");
        if (hr == D3DNative.EOutOfMemory) throw new GpuComputeException(BenchErrorKeys.VramAllocFailed, hr, $"{what}: out of memory");
        throw new GpuComputeException(BenchErrorKeys.Failed, hr, $"{what} 0x{hr:X8}");
    }

    public void Dispose()
    {
        try { ClearState(); Flush(); }
        catch (ObjectDisposedException) { }
        _context.Dispose();
        _device.Dispose();
        _adapter3?.Dispose();
        _adapter.Dispose();
    }
}
