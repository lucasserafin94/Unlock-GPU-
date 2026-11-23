using System.Runtime.InteropServices;
using UnlockGpu.Core.Model;

namespace UnlockGpu.Core.Nvml;

/// <summary>Acesso a GPU. Abstraido para testes (FakeGpu) e para isolar a NVML.</summary>
public interface IGpu : IDisposable
{
    bool IsAvailable { get; }
    string LastError { get; }
    GpuInfo? Info { get; }
    GpuSample Sample();
    double EnforcedLimitW();
    /// <summary>Configured (management) limit; NaN when the driver does not expose it.</summary>
    double ConfiguredLimitW() => double.NaN;
    /// <summary>Codigo NVML (0 = ok). Precisa de administrador.</summary>
    int SetPowerLimitW(double watts);
    OffsetInfo? GetOffset(ClockDomain domain);
    int SetOffset(ClockDomain domain, int mhz);
    int LockMemoryClock(uint mhz);
    int ResetMemoryClock();
    string ErrorText(int rc);
}

/// <summary>Implementacao real sobre nvml.dll. Todas as chamadas sao serializadas (a NVML aceita multithread,
/// mas o reabrir apos reinicio do driver nao pode correr em paralelo).</summary>
public sealed class NvmlGpu : IGpu
{
    readonly Lock _gate = new();
    IntPtr _dev;
    bool _init;
    GpuInfo? _info;

    public bool IsAvailable { get { lock (_gate) return EnsureInit(); } }
    public string LastError { get; private set; } = "";
    public GpuInfo? Info { get { lock (_gate) { if (_info == null && EnsureInit()) _info = ReadInfo(); return _info; } } }

    bool EnsureInit()
    {
        if (_init) return true;
        try
        {
            int rc = NvmlNative.Init();
            if (rc != NvmlNative.Success) { LastError = "nvmlInit: " + ErrorTextUnsafe(rc); return false; }
            rc = NvmlNative.GetHandleByIndex(0, out _dev);
            if (rc != NvmlNative.Success) { LastError = "GetHandle: " + ErrorTextUnsafe(rc); _ = NvmlNative.Shutdown(); return false; }
            _init = true; LastError = "";
            return true;
        }
        catch (DllNotFoundException) { LastError = "nvml.dll not found (NVIDIA driver missing)"; return false; }
        catch (EntryPointNotFoundException e) { LastError = e.Message; return false; }
    }

    void Reinit()
    {
        try { if (_init) _ = NvmlNative.Shutdown(); } catch (DllNotFoundException) { }
        _init = false; _dev = IntPtr.Zero; _info = null;
    }

    GpuInfo ReadInfo()
    {
        var b1 = new byte[96]; string name = NvmlNative.GetName(_dev, b1, 96) == 0 ? NvmlNative.CString(b1) : "?";
        var b2 = new byte[80]; string drv = NvmlNative.GetDriverVersion(b2, 80) == 0 ? NvmlNative.CString(b2) : "?";
        var b3 = new byte[32]; string vb = NvmlNative.GetVbiosVersion(_dev, b3, 32) == 0 ? NvmlNative.CString(b3) : "?";
        double mn = double.NaN, mx = double.NaN, df = double.NaN;
        if (NvmlNative.GetLimitConstraints(_dev, out uint a, out uint b) == 0) { mn = a / 1000.0; mx = b / 1000.0; }
        if (NvmlNative.GetDefaultLimit(_dev, out uint d) == 0) df = d / 1000.0;
        uint maxCore = NvmlNative.GetMaxClockInfo(_dev, NvmlNative.ClockGraphics, out uint mc) == 0 ? mc : 0;
        return new GpuInfo(name, drv, vb, mn, df, mx, maxCore, ReadMemClocks());
    }

    uint[] ReadMemClocks()
    {
        uint n = 0;
        int rc = NvmlNative.GetSupportedMemClocks(_dev, ref n, null);
        if ((rc != NvmlNative.ErrorInsufficientSize && rc != NvmlNative.Success) || n == 0) return [];
        var arr = new uint[n];
        if (NvmlNative.GetSupportedMemClocks(_dev, ref n, arr) != 0) return [];
        return arr.Take((int)n).OrderByDescending(x => x).ToArray();
    }

    public GpuSample Sample()
    {
        lock (_gate)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!EnsureInit()) return new GpuSample { Time = DateTime.Now, Ok = false, Error = LastError };
                int rp = NvmlNative.GetPowerUsage(_dev, out uint mw);
                if (rp != 0 && attempt == 0 && NvmlNative.NeedsReinit(rp)) { Reinit(); continue; }
                int rm = NvmlNative.GetClockInfo(_dev, NvmlNative.ClockMem, out uint mem);
                ulong reasons;
                try { if (NvmlNative.GetEventReasons(_dev, out reasons) != 0) reasons = 0; }
                catch (EntryPointNotFoundException) { if (NvmlNative.GetThrottleReasons(_dev, out reasons) != 0) reasons = 0; }
                NvmlNative.Memory vm = default;
                bool memOk = NvmlNative.GetMemoryInfo(_dev, out vm) == 0;
                NvmlNative.Utilization u = default;
                bool utilOk = NvmlNative.GetUtilization(_dev, out u) == 0;
                return new GpuSample
                {
                    Time = DateTime.Now,
                    PowerW = rp == 0 ? mw / 1000.0 : double.NaN,
                    LimitW = NvmlNative.GetEnforcedPowerLimit(_dev, out uint lim) == 0 ? lim / 1000.0 : double.NaN,
                    CoreMHz = NvmlNative.GetClockInfo(_dev, NvmlNative.ClockGraphics, out uint core) == 0 ? core : 0,
                    SmMHz = NvmlNative.GetClockInfo(_dev, NvmlNative.ClockSm, out uint sm) == 0 ? sm : 0,
                    MemMHz = rm == 0 ? mem : 0,
                    TempC = NvmlNative.GetTemperature(_dev, NvmlNative.TempGpu, out uint t) == 0 ? t : 0,
                    PState = NvmlNative.GetPState(_dev, out int ps) == 0 && ps is >= 0 and <= 15 ? "P" + ps : "?",
                    UtilGpu = utilOk ? u.Gpu : 0,
                    UtilMem = utilOk ? u.Memory : 0,
                    VramUsedMB = memOk ? vm.Used / 1048576.0 : 0,
                    VramTotalMB = memOk ? vm.Total / 1048576.0 : 0,
                    Reasons = (ClockReasons)reasons,
                    Ok = rp == 0 || rm == 0,
                    Error = rp == 0 ? null : "PowerUsage: " + ErrorTextUnsafe(rp),
                };
            }
            return new GpuSample { Time = DateTime.Now, Ok = false, Error = LastError };
        }
    }

    public double EnforcedLimitW()
    {
        lock (_gate) return EnsureInit() && NvmlNative.GetEnforcedPowerLimit(_dev, out uint v) == 0 ? v / 1000.0 : double.NaN;
    }

    public double ConfiguredLimitW()
    {
        lock (_gate) return EnsureInit() && NvmlNative.GetPowerLimit(_dev, out uint v) == 0 ? v / 1000.0 : double.NaN;
    }

    /// <summary>Runs a write; if the handle went stale (driver restarted), re-opens NVML once and retries.</summary>
    int Write(Func<IntPtr, int> call)
    {
        if (!EnsureInit()) return -1;
        int rc = call(_dev);
        if (NvmlNative.NeedsReinit(rc)) { Reinit(); if (!EnsureInit()) return -1; rc = call(_dev); }
        return rc;
    }

    public int SetPowerLimitW(double watts)
    {
        if (double.IsNaN(watts) || watts <= 0) return NvmlNative.ErrorInvalidArgument;
        uint mw = (uint)Math.Round(watts * 1000.0);
        lock (_gate) return Write(d => NvmlNative.SetPowerLimit(d, mw));
    }

    public OffsetInfo? GetOffset(ClockDomain domain)
    {
        lock (_gate)
        {
            if (!EnsureInit()) return null;
            var o = new NvmlNative.ClockOffset { Version = NvmlNative.ClockOffsetVersion, Type = (int)domain, PState = 0 };
            return NvmlNative.GetClockOffsets(_dev, ref o) == 0 ? new OffsetInfo(o.OffsetMHz, o.MinMHz, o.MaxMHz) : null;
        }
    }

    public int SetOffset(ClockDomain domain, int mhz)
    {
        lock (_gate)
        {
            return Write(d =>
            {
                var o = new NvmlNative.ClockOffset { Version = NvmlNative.ClockOffsetVersion, Type = (int)domain, PState = 0, OffsetMHz = mhz };
                return NvmlNative.SetClockOffsets(d, ref o);
            });
        }
    }

    public int LockMemoryClock(uint mhz) { lock (_gate) return Write(d => NvmlNative.SetMemoryLockedClocks(d, mhz, mhz)); }
    public int ResetMemoryClock() { lock (_gate) return Write(d => NvmlNative.ResetMemoryLockedClocks(d)); }

    public string ErrorText(int rc) { lock (_gate) return ErrorTextUnsafe(rc); }

    static string ErrorTextUnsafe(int rc)
    {
        try { return (Marshal.PtrToStringAnsi(NvmlNative.ErrorString(rc)) ?? "?") + $" ({rc})"; }
        catch (DllNotFoundException) { return $"NVML error {rc}"; }
    }

    public void Dispose() { lock (_gate) Reinit(); }
}
