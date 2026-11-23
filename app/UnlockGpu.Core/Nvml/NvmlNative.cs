using System.Runtime.InteropServices;

namespace UnlockGpu.Core.Nvml;

/// <summary>P/Invoke para nvml.dll (API publica: https://docs.nvidia.com/deploy/nvml-api/).
/// A DLL vem com o driver da NVIDIA e nunca e distribuida com o app.</summary>
internal static partial class NvmlNative
{
    const string Dll = "nvml.dll";

    public const int Success = 0;
    public const int ErrorInvalidArgument = 2;
    public const int ErrorNotSupported = 3;
    public const int ErrorNoPermission = 4;
    public const int ErrorInsufficientSize = 7;

    [StructLayout(LayoutKind.Sequential)]
    public struct Utilization { public uint Gpu; public uint Memory; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Memory { public ulong Total; public ulong Free; public ulong Used; }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClockOffset { public uint Version; public int Type; public int PState; public int OffsetMHz; public int MinMHz; public int MaxMHz; }

    public static uint ClockOffsetVersion => (uint)(Marshal.SizeOf<ClockOffset>() | (1 << 24));

    [DllImport(Dll, EntryPoint = "nvmlInit_v2")] public static extern int Init();
    [DllImport(Dll, EntryPoint = "nvmlShutdown")] public static extern int Shutdown();
    [DllImport(Dll, EntryPoint = "nvmlErrorString")] public static extern IntPtr ErrorString(int result);
    [DllImport(Dll, EntryPoint = "nvmlSystemGetDriverVersion")] public static extern int GetDriverVersion([Out] byte[] version, uint length);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] public static extern int GetHandleByIndex(uint index, out IntPtr device);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetName")] public static extern int GetName(IntPtr device, [Out] byte[] name, uint length);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetVbiosVersion")] public static extern int GetVbiosVersion(IntPtr device, [Out] byte[] version, uint length);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerUsage")] public static extern int GetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")] public static extern int GetEnforcedPowerLimit(IntPtr device, out uint milliwatts);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerManagementLimitConstraints")] public static extern int GetLimitConstraints(IntPtr device, out uint minMw, out uint maxMw);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerManagementDefaultLimit")] public static extern int GetDefaultLimit(IntPtr device, out uint milliwatts);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerManagementLimit")] public static extern int GetPowerLimit(IntPtr device, out uint milliwatts);
    [DllImport(Dll, EntryPoint = "nvmlDeviceSetPowerManagementLimit")] public static extern int SetPowerLimit(IntPtr device, uint milliwatts);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetClockInfo")] public static extern int GetClockInfo(IntPtr device, int type, out uint mhz);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetMaxClockInfo")] public static extern int GetMaxClockInfo(IntPtr device, int type, out uint mhz);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetTemperature")] public static extern int GetTemperature(IntPtr device, int sensor, out uint celsius);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetUtilizationRates")] public static extern int GetUtilization(IntPtr device, out Utilization util);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPerformanceState")] public static extern int GetPState(IntPtr device, out int pstate);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetCurrentClocksEventReasons")] public static extern int GetEventReasons(IntPtr device, out ulong reasons);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetCurrentClocksThrottleReasons")] public static extern int GetThrottleReasons(IntPtr device, out ulong reasons);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetSupportedMemoryClocks")] public static extern int GetSupportedMemClocks(IntPtr device, ref uint count, [Out] uint[]? clocks);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetMemoryInfo")] public static extern int GetMemoryInfo(IntPtr device, out Memory memory);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetClockOffsets")] public static extern int GetClockOffsets(IntPtr device, ref ClockOffset offset);
    [DllImport(Dll, EntryPoint = "nvmlDeviceSetClockOffsets")] public static extern int SetClockOffsets(IntPtr device, ref ClockOffset offset);
    [DllImport(Dll, EntryPoint = "nvmlDeviceSetMemoryLockedClocks")] public static extern int SetMemoryLockedClocks(IntPtr device, uint minMHz, uint maxMHz);
    [DllImport(Dll, EntryPoint = "nvmlDeviceResetMemoryLockedClocks")] public static extern int ResetMemoryLockedClocks(IntPtr device);

    /// <summary>Le uma string C (ASCII/UTF-8) de um buffer preenchido pela NVML.</summary>
    public static string CString(byte[] buf)
    {
        int n = Array.IndexOf(buf, (byte)0);
        return System.Text.Encoding.UTF8.GetString(buf, 0, n < 0 ? buf.Length : n);
    }

    public const int ClockGraphics = 0, ClockSm = 1, ClockMem = 2;
    public const int TempGpu = 0;

    /// <summary>Codigos que indicam handle invalido (driver reiniciado): vale reabrir a NVML.</summary>
    public static bool NeedsReinit(int rc) => rc is 1 or 2 or 15 or 999;
}
