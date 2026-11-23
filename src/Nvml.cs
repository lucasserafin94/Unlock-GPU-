// Leitura da GPU pela NVML (biblioteca oficial e documentada da NVIDIA: nvml.dll).
// Referencia publica: https://docs.nvidia.com/deploy/nvml-api/
// Compativel com C# 5 (compilado pelo Add-Type do Windows PowerShell 5.1).
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace UnlockGpu
{
    public class GpuSample
    {
        public DateTime Time;
        public string PState;          // "P0".."P15"
        public uint CoreMHz;
        public uint SmMHz;
        public uint MemMHz;
        public double VramUsedMB;      // memoria de video em uso
        public double VramTotalMB;
        public double PowerW;          // consumo atual
        public double LimitW;          // limite em vigor (enforced)
        public uint TempC;
        public uint UtilGpu;
        public uint UtilMem;
        public ulong Reasons;          // mascara de motivos de corte de clock
        public bool Ok;
        public string Error;
    }

    public static class Nvml
    {
        const string Dll = "nvml.dll";
        public const int NVML_SUCCESS = 0;

        [StructLayout(LayoutKind.Sequential)]
        struct NvmlUtilization { public uint gpu; public uint memory; }

        [StructLayout(LayoutKind.Sequential)]
        struct NvmlMemory { public ulong total; public ulong free; public ulong used; }

        [DllImport(Dll, EntryPoint = "nvmlDeviceSetPowerManagementLimit")] static extern int SetPowerLimit(IntPtr device, uint milliwatts);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerManagementLimit")] static extern int GetPowerLimit(IntPtr device, out uint milliwatts);
        [StructLayout(LayoutKind.Sequential)]
        public struct ClockOffset { public uint version; public int type; public int pstate; public int offsetMHz; public int minMHz; public int maxMHz; }
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetClockOffsets")] static extern int GetClockOffsets(IntPtr device, ref ClockOffset o);
        [DllImport(Dll, EntryPoint = "nvmlDeviceSetClockOffsets")] static extern int SetClockOffsets(IntPtr device, ref ClockOffset o);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetGpcClkMinMaxVfOffset")] static extern int GetGpcOffsetRange(IntPtr device, out int min, out int max);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetMemClkMinMaxVfOffset")] static extern int GetMemOffsetRange(IntPtr device, out int min, out int max);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetGpcClkVfOffset")] static extern int GetGpcOffset(IntPtr device, out int offset);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetMemClkVfOffset")] static extern int GetMemOffset(IntPtr device, out int offset);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetMemoryInfo")] static extern int GetMemoryInfo(IntPtr device, out NvmlMemory mem);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetVbiosVersion")] static extern int GetVbiosVersion(IntPtr device, StringBuilder version, uint length);

        [DllImport(Dll, EntryPoint = "nvmlInit_v2")] static extern int nvmlInit();
        [DllImport(Dll, EntryPoint = "nvmlShutdown")] static extern int nvmlShutdown();
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] static extern int GetHandle(uint index, out IntPtr device);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetName")] static extern int GetName(IntPtr device, StringBuilder name, uint length);
        [DllImport(Dll, EntryPoint = "nvmlSystemGetDriverVersion")] static extern int GetDriverVersion(StringBuilder version, uint length);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerUsage")] static extern int GetPowerUsage(IntPtr device, out uint milliwatts);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")] static extern int GetEnforcedPowerLimit(IntPtr device, out uint milliwatts);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerManagementLimitConstraints")] static extern int GetLimitConstraints(IntPtr device, out uint minMw, out uint maxMw);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerManagementDefaultLimit")] static extern int GetDefaultLimit(IntPtr device, out uint milliwatts);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetClockInfo")] static extern int GetClockInfo(IntPtr device, int type, out uint mhz);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetMaxClockInfo")] static extern int GetMaxClockInfo(IntPtr device, int type, out uint mhz);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetTemperature")] static extern int GetTemperature(IntPtr device, int sensor, out uint celsius);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetUtilizationRates")] static extern int GetUtilization(IntPtr device, out NvmlUtilization util);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetPerformanceState")] static extern int GetPState(IntPtr device, out int pstate);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetCurrentClocksEventReasons")] static extern int GetEventReasons(IntPtr device, out ulong reasons);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetCurrentClocksThrottleReasons")] static extern int GetThrottleReasons(IntPtr device, out ulong reasons);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetSupportedMemoryClocks")] static extern int GetSupportedMemClocks(IntPtr device, ref uint count, [Out] uint[] clocks);
        [DllImport(Dll, EntryPoint = "nvmlErrorString")] static extern IntPtr ErrorString(int result);

        const int CLOCK_GRAPHICS = 0, CLOCK_SM = 1, CLOCK_MEM = 2;
        const int TEMP_GPU = 0;

        static IntPtr _dev = IntPtr.Zero;
        static bool _init;
        public static string LastError = "";

        // UNINITIALIZED=1, INVALID_ARGUMENT=2 (handle velho), GPU_IS_LOST=15, UNKNOWN=999
        static bool NeedsReset(int r) { return r == 1 || r == 2 || r == 15 || r == 999; }

        public static string Err(int r)
        {
            try { return Marshal.PtrToStringAnsi(ErrorString(r)) + " (" + r + ")"; } catch { return "NVML erro " + r; }
        }

        public static bool Init()
        {
            if (_init) return true;
            try
            {
                int r = nvmlInit();
                if (r != NVML_SUCCESS) { LastError = "nvmlInit: " + Err(r); return false; }
                r = GetHandle(0, out _dev);
                if (r != NVML_SUCCESS) { LastError = "GetHandle: " + Err(r); nvmlShutdown(); return false; }
                _init = true;
                return true;
            }
            catch (Exception e) { LastError = e.Message; return false; }
        }

        // Depois de um reinicio do driver o handle pode ficar invalido: reabre.
        public static void Reset()
        {
            try { if (_init) nvmlShutdown(); } catch { }
            _init = false; _dev = IntPtr.Zero;
        }

        public static void Shutdown() { Reset(); }

        public static string Name()
        {
            if (!Init()) return "";
            var sb = new StringBuilder(96);
            return GetName(_dev, sb, 96) == NVML_SUCCESS ? sb.ToString() : "";
        }

        public static string Driver()
        {
            if (!Init()) return "";
            var sb = new StringBuilder(80);
            return GetDriverVersion(sb, 80) == NVML_SUCCESS ? sb.ToString() : "";
        }

        public static string Vbios()
        {
            if (!Init()) return "";
            var sb = new StringBuilder(32);
            return GetVbiosVersion(_dev, sb, 32) == NVML_SUCCESS ? sb.ToString() : "";
        }

        public static double[] PowerLimits() // {min, padrao, max} em W
        {
            if (!Init()) return null;
            uint mn, mx, df;
            if (GetLimitConstraints(_dev, out mn, out mx) != NVML_SUCCESS) return null;
            if (GetDefaultLimit(_dev, out df) != NVML_SUCCESS) df = 0;
            return new double[] { mn / 1000.0, df / 1000.0, mx / 1000.0 };
        }

        // Limite configurado (o que foi pedido) e limite em vigor (o que o driver aplica).
        public static double ConfiguredLimitW() { uint v; return (Init() && GetPowerLimit(_dev, out v) == NVML_SUCCESS) ? v / 1000.0 : double.NaN; }
        public static double EnforcedLimitW() { uint v; return (Init() && GetEnforcedPowerLimit(_dev, out v) == NVML_SUCCESS) ? v / 1000.0 : double.NaN; }

        // API documentada: aceita so dentro de PowerManagementLimitConstraints. Precisa de administrador.
        // Retorna o codigo NVML (0 = ok). A leitura de volta e feita por quem chama.
        public static int SetPowerLimitW(double watts)
        {
            if (!Init()) return -1;
            if (double.IsNaN(watts) || watts <= 0) return 2; // INVALID_ARGUMENT
            return SetPowerLimit(_dev, (uint)Math.Round(watts * 1000.0));
        }

        // Offsets de clock pela API v555+ (nvmlDeviceGet/SetClockOffsets). domain: 0 = nucleo (graphics), 2 = memoria.
        // Retorna {offset, min, max} em MHz, ou null se o driver nao suportar.
        static ClockOffset NewOffset(int domain, int pstate)
        {
            var o = new ClockOffset();
            o.version = (uint)(Marshal.SizeOf(typeof(ClockOffset)) | (1 << 24));
            o.type = domain; o.pstate = pstate;
            return o;
        }
        public static int[] ClockOffsetInfo(int domain)
        {
            if (!Init()) return null;
            var o = NewOffset(domain, 0);
            return GetClockOffsets(_dev, ref o) == NVML_SUCCESS ? new int[] { o.offsetMHz, o.minMHz, o.maxMHz } : null;
        }
        // Precisa de administrador. O driver aplica o offset a todos os niveis (P0/P2...).
        public static int SetClockOffsetMHz(int domain, int mhz)
        {
            if (!Init()) return -1;
            var o = NewOffset(domain, 0);
            o.offsetMHz = mhz;
            return SetClockOffsets(_dev, ref o);
        }

        // Faixas e valores atuais de offset (API antiga, obsoleta; no Windows costuma dar Not Supported).
        public static int[] CoreOffsetRange() { int a, b; return (Init() && GetGpcOffsetRange(_dev, out a, out b) == NVML_SUCCESS) ? new int[] { a, b } : null; }
        public static int[] MemOffsetRange() { int a, b; return (Init() && GetMemOffsetRange(_dev, out a, out b) == NVML_SUCCESS) ? new int[] { a, b } : null; }
        public static int? CoreOffset() { int v; return (Init() && GetGpcOffset(_dev, out v) == NVML_SUCCESS) ? (int?)v : null; }
        public static int? MemOffset() { int v; return (Init() && GetMemOffset(_dev, out v) == NVML_SUCCESS) ? (int?)v : null; }

        public static uint MaxCoreMHz() { uint v; return (Init() && GetMaxClockInfo(_dev, CLOCK_GRAPHICS, out v) == NVML_SUCCESS) ? v : 0; }

        public static uint[] SupportedMemClocks()
        {
            if (!Init()) return new uint[0];
            uint n = 0;
            int r0 = GetSupportedMemClocks(_dev, ref n, null);
            if ((r0 != 7 && r0 != NVML_SUCCESS) || n == 0) return new uint[0]; // 7 = INSUFFICIENT_SIZE, n = quantos existem
            var arr = new uint[n];
            if (GetSupportedMemClocks(_dev, ref n, arr) != NVML_SUCCESS) return new uint[0];
            var res = new uint[n]; Array.Copy(arr, res, (int)n); Array.Sort(res); Array.Reverse(res);
            return res;
        }

        public static GpuSample Sample()
        {
            var s = new GpuSample { Time = DateTime.Now };
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!Init()) { s.Ok = false; s.Error = LastError; return s; }
                uint mw, lim, core, mem, t; int ps; NvmlUtilization u; ulong rs;
                int r = GetPowerUsage(_dev, out mw);
                // So reabre a NVML quando o handle ficou invalido (driver reiniciado), nao em "nao suportado".
                if (r != NVML_SUCCESS && attempt == 0 && NeedsReset(r)) { Reset(); continue; }
                s.PowerW = r == NVML_SUCCESS ? mw / 1000.0 : double.NaN;
                s.LimitW = GetEnforcedPowerLimit(_dev, out lim) == NVML_SUCCESS ? lim / 1000.0 : double.NaN;
                s.CoreMHz = GetClockInfo(_dev, CLOCK_GRAPHICS, out core) == NVML_SUCCESS ? core : 0;
                uint sm; s.SmMHz = GetClockInfo(_dev, CLOCK_SM, out sm) == NVML_SUCCESS ? sm : 0;
                NvmlMemory vm;
                if (GetMemoryInfo(_dev, out vm) == NVML_SUCCESS) { s.VramUsedMB = vm.used / 1048576.0; s.VramTotalMB = vm.total / 1048576.0; }
                int rm = GetClockInfo(_dev, CLOCK_MEM, out mem);
                s.MemMHz = rm == NVML_SUCCESS ? mem : 0;
                s.TempC = GetTemperature(_dev, TEMP_GPU, out t) == NVML_SUCCESS ? t : 0;
                s.PState = (GetPState(_dev, out ps) == NVML_SUCCESS && ps >= 0 && ps <= 15) ? ("P" + ps) : "?";
                if (GetUtilization(_dev, out u) == NVML_SUCCESS) { s.UtilGpu = u.gpu; s.UtilMem = u.memory; }
                try { s.Reasons = GetEventReasons(_dev, out rs) == NVML_SUCCESS ? rs : 0; }
                catch (EntryPointNotFoundException) { s.Reasons = GetThrottleReasons(_dev, out rs) == NVML_SUCCESS ? rs : 0; }
                s.Ok = (r == NVML_SUCCESS) || (rm == NVML_SUCCESS);
                if (r != NVML_SUCCESS) s.Error = "PowerUsage: " + Err(r);
                return s;
            }
            s.Ok = false; s.Error = LastError; return s;
        }

        // Motivos de corte de clock (nvmlClocksEventReasons) como lista de etiquetas.
        // Cada item: "nivel|texto"; nivel = alerta (corta desempenho), info (normal/esperado).
        public static string[] ReasonsList(ulong m)
        {
            var parts = new System.Collections.Generic.List<string>();
            if ((m & 0x4UL) != 0) parts.Add("alerta|Limitado por potência (SW cap)");
            if ((m & 0x80UL) != 0) parts.Add("alerta|Freio de energia (hardware)");
            if ((m & 0x40UL) != 0) parts.Add("alerta|Térmico (hardware)");
            if ((m & 0x20UL) != 0) parts.Add("alerta|Térmico (software)");
            if ((m & 0x8UL) != 0) parts.Add("alerta|Corte de hardware");
            if ((m & 0x200UL) != 0) parts.Add("alerta|Limite da placa");
            if ((m & 0x2UL) != 0) parts.Add("info|Clock fixado por app");
            if ((m & 0x10UL) != 0) parts.Add("info|Sync boost");
            if ((m & 0x100UL) != 0) parts.Add("info|Clock de tela");
            if ((m & 0x400UL) != 0) parts.Add("info|Confiabilidade");
            if ((m & 0x1UL) != 0) parts.Add("info|Ociosa");
            ulong known = 0x1UL | 0x2UL | 0x4UL | 0x8UL | 0x10UL | 0x20UL | 0x40UL | 0x80UL | 0x100UL | 0x200UL | 0x400UL;
            if ((m & ~known) != 0) parts.Add("info|Outro (0x" + (m & ~known).ToString("X") + ")");
            if (parts.Count == 0) parts.Add("ok|Nenhum corte");
            return parts.ToArray();
        }

        public static string ReasonsText(ulong m)
        {
            var l = ReasonsList(m);
            for (int i = 0; i < l.Length; i++) l[i] = l[i].Substring(l[i].IndexOf('|') + 1);
            return string.Join(" + ", l);
        }
    }
}
