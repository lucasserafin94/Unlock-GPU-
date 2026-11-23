# Leitura (somente) dos offsets de clock pela NVML, com o codigo de retorno de cada chamada.
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class NvProbe {
  [StructLayout(LayoutKind.Sequential)]
  public struct ClockOffset { public uint version; public int type; public int pstate; public int offsetMHz; public int minMHz; public int maxMHz; }
  [DllImport("nvml.dll")] public static extern int nvmlInit_v2();
  [DllImport("nvml.dll")] public static extern int nvmlDeviceGetHandleByIndex_v2(uint i, out IntPtr d);
  [DllImport("nvml.dll")] public static extern int nvmlDeviceGetGpcClkMinMaxVfOffset(IntPtr d, out int a, out int b);
  [DllImport("nvml.dll")] public static extern int nvmlDeviceGetMemClkMinMaxVfOffset(IntPtr d, out int a, out int b);
  [DllImport("nvml.dll")] public static extern int nvmlDeviceGetClockOffsets(IntPtr d, ref ClockOffset o);
}
"@
[void][NvProbe]::nvmlInit_v2(); $d = [IntPtr]::Zero; [void][NvProbe]::nvmlDeviceGetHandleByIndex_v2(0, [ref]$d)
$a = 0; $b = 0; "deprecated gpc range rc=$([NvProbe]::nvmlDeviceGetGpcClkMinMaxVfOffset($d, [ref]$a, [ref]$b))"
$a = 0; $b = 0; "deprecated mem range rc=$([NvProbe]::nvmlDeviceGetMemClkMinMaxVfOffset($d, [ref]$a, [ref]$b))"
$size = [Runtime.InteropServices.Marshal]::SizeOf([type][NvProbe+ClockOffset])
foreach ($t in @(@{n='graphics';v=0}, @{n='memory';v=2})) {
  foreach ($ps in 0, 2) {
    $o = New-Object NvProbe+ClockOffset
    $o.version = [uint32]($size -bor (1 -shl 24)); $o.type = $t.v; $o.pstate = $ps
    $rc = [NvProbe]::nvmlDeviceGetClockOffsets($d, [ref]$o)
    "GetClockOffsets {0,-8} P{1} rc={2}  offset={3}  faixa={4} .. {5}" -f $t.n, $ps, $rc, $o.offsetMHz, $o.minMHz, $o.maxMHz
  }
}
