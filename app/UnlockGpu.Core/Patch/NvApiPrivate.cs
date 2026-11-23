// ============================================================================
// NvApiPrivate.cs — Unlock GPU · patch kit
// Namespace: UnlockGpu.Patch (C# 5 safe for PowerShell 5.1 Add-Type)
//
// MECANISMO: nvapi64.dll exporta nvapi_QueryInterface, que devolve ponteiros de
// função por ID numérico. Os IDs públicos cobrem init/enum/unload; os métodos
// privados do RM (ex.: Type19 rail policy) também resolvem pelo mesmo QI quando
// o driver os exporta no catálogo. Se um driver não exporta o ID, o fallback é
// escanear nvapi64_impl.dll em busca da tabela de dispatch — ver
// docs/MAPA-ORIGEM.md (v2).
// ============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace UnlockGpu.Patch
{
    public sealed class NvApiPrivate : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr QueryInterfaceDelegate(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InitDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int EnumGpuDelegate(IntPtr handles, ref uint count);

        // Contrato genérico dos métodos RM: (gpuHandle, buffer) -> rc
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int GpuBufferDelegate(IntPtr gpu, IntPtr data);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetBusIdDelegate(IntPtr gpu, ref uint busId);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PciIdentifiersDelegate(IntPtr gpu, ref uint deviceId, ref uint subsystemId, ref uint revisionId, ref uint extDeviceId);

        // QI IDs públicos (estáveis entre drivers).
        public const uint QI_INIT = 22079528u;                 // NvAPI_Initialize
        public const uint QI_ENUM_PHYSICAL_GPUS = 3853292063u; // NvAPI_EnumPhysicalGPUs
        public const uint QI_UNLOAD = 3526090110u;             // NvAPI_Uninitialize
        private const uint QI_GET_BUS_ID = 467712229u;         // GetBusId
        private const uint QI_GET_PCI_IDENTIFIERS = 769635950u;// PciIdentifiers

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);

        public QueryInterfaceDelegate QI;
        public IntPtr Gpu;
        public int PhysicalGpuCount;
        public string ImplPath = "";
        public IntPtr ImplBase;

        private IntPtr _shim;
        private InitDelegate _unload;

        // pciBus < 0 ou deviceId == 0 → aceita a única GPU física enumerada.
        public bool Open(int pciBus, int deviceId, out string error)
        {
            error = "";
            string shimPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvapi64.dll");
            _shim = LoadLibrary(shimPath);
            if (_shim == IntPtr.Zero)
            {
                error = "nvapi64.dll could not be loaded (" + shimPath + ").";
                return false;
            }
            IntPtr procAddress = GetProcAddress(_shim, "nvapi_QueryInterface");
            if (procAddress == IntPtr.Zero)
            {
                error = "nvapi_QueryInterface is missing from nvapi64.dll.";
                return false;
            }
            QI = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(procAddress);

            IntPtr initPtr = QI(QI_INIT);
            IntPtr enumPtr = QI(QI_ENUM_PHYSICAL_GPUS);
            if (initPtr == IntPtr.Zero || enumPtr == IntPtr.Zero)
            {
                error = "NvAPI init/enum interfaces not exported by this driver.";
                return false;
            }
            InitDelegate init = Marshal.GetDelegateForFunctionPointer<InitDelegate>(initPtr);
            EnumGpuDelegate enumerate = Marshal.GetDelegateForFunctionPointer<EnumGpuDelegate>(enumPtr);
            IntPtr unloadPtr = QI(QI_UNLOAD);
            if (unloadPtr != IntPtr.Zero)
            {
                _unload = Marshal.GetDelegateForFunctionPointer<InitDelegate>(unloadPtr);
            }

            int rc = init();
            if (rc != 0)
            {
                error = "NvAPI_Initialize failed rc=" + rc;
                return false;
            }

            IntPtr handles = Marshal.AllocHGlobal(IntPtr.Size * 64);
            try
            {
                uint count = 64;
                rc = enumerate(handles, ref count);
                PhysicalGpuCount = (int)count;
                if (rc != 0 || count == 0)
                {
                    error = "NvAPI_EnumPhysicalGPUs failed rc=" + rc + ", count=" + count;
                    return false;
                }

                IntPtr selected = IntPtr.Zero;
                int matches = 0;
                for (uint i = 0; i < count; i++)
                {
                    IntPtr gpu = Marshal.ReadIntPtr(handles, (int)i * IntPtr.Size);
                    if (MatchesRequestedGpu(gpu, pciBus, deviceId))
                    {
                        selected = gpu;
                        matches++;
                    }
                }

                if (pciBus < 0 && deviceId == 0)
                {
                    // Sem filtro: exige exatamente uma GPU física.
                    if (count != 1)
                    {
                        error = "NvAPI enumerated " + count + " physical GPUs; pass an exact PCI bus/device.";
                        return false;
                    }
                    selected = Marshal.ReadIntPtr(handles, 0);
                }
                else if (matches != 1 || selected == IntPtr.Zero)
                {
                    error = "NvAPI GPU mapping is " + (matches == 0 ? "unresolved" : "ambiguous") + ". matches=" + matches;
                    return false;
                }

                Gpu = selected;
            }
            finally
            {
                Marshal.FreeHGlobal(handles);
            }

            // Registra o módulo de implementação (útil para o fallback v2 de scan).
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                if (module.ModuleName.Equals("nvapi64_impl.dll", StringComparison.OrdinalIgnoreCase))
                {
                    ImplPath = module.FileName;
                    ImplBase = module.BaseAddress;
                    break;
                }
            }
            return true;
        }

        private bool MatchesRequestedGpu(IntPtr gpu, int pciBus, int deviceId)
        {
            if (pciBus < 0 && deviceId == 0)
            {
                return false; // caminho de GPU única é tratado em Open()
            }
            bool matched = false;
            IntPtr busPtr = QI(QI_GET_BUS_ID);
            if (busPtr != IntPtr.Zero && pciBus >= 0)
            {
                GetBusIdDelegate getBusId = Marshal.GetDelegateForFunctionPointer<GetBusIdDelegate>(busPtr);
                uint busId = 0;
                if (getBusId(gpu, ref busId) == 0)
                {
                    matched = true;
                    if (busId != (uint)pciBus)
                    {
                        return false;
                    }
                }
            }
            IntPtr pciPtr = QI(QI_GET_PCI_IDENTIFIERS);
            if (pciPtr != IntPtr.Zero && deviceId > 0)
            {
                PciIdentifiersDelegate getPci = Marshal.GetDelegateForFunctionPointer<PciIdentifiersDelegate>(pciPtr);
                uint devId = 0;
                uint subSysId = 0;
                uint revId = 0;
                uint extDevId = 0;
                if (getPci(gpu, ref devId, ref subSysId, ref revId, ref extDevId) == 0)
                {
                    int normalized = NormalizeDeviceId(devId);
                    matched = true;
                    if (normalized != deviceId)
                    {
                        return false;
                    }
                }
            }
            return matched;
        }

        // Alguns drivers devolvem vendor<<16|device: se a metade baixa for 0x10DE (4318), o device id real está na metade alta.
        private static int NormalizeDeviceId(uint raw)
        {
            int low = (int)(raw & 0xFFFF);
            if (low == 4318)
            {
                return (int)((raw >> 16) & 0xFFFF);
            }
            return low;
        }

        // Resolve um método RM privado pelo ID numérico via QI direto.
        // Retorna null quando este driver não exporta o ID no catálogo — nesse caso, ver MAPA-ORIGEM.md (fallback v2: scan de nvapi64_impl.dll).
        public GpuBufferDelegate Resolve(uint methodId)
        {
            if (QI == null)
            {
                return null;
            }
            IntPtr ptr = QI(methodId);
            if (ptr == IntPtr.Zero)
            {
                return null;
            }
            return Marshal.GetDelegateForFunctionPointer<GpuBufferDelegate>(ptr);
        }

        public void Dispose()
        {
            try
            {
                if (_unload != null)
                {
                    _unload();
                }
            }
            catch
            {
                // unload é best-effort
            }
            if (_shim != IntPtr.Zero)
            {
                FreeLibrary(_shim);
                _shim = IntPtr.Zero;
            }
        }
    }
}
