# Precisa de administrador. Reaplica os offsets atuais (sem mudanca real) e le de volta.
$o = 'G:\TravaVRAM\tests\out\offsets-noop.txt'
New-Item -ItemType Directory -Force (Split-Path $o) | Out-Null
try {
    Add-Type -TypeDefinition (Get-Content 'G:\TravaVRAM\src\Nvml.cs' -Raw) -Language CSharp
    [void][UnlockGpu.Nvml]::Init()
    $c = [UnlockGpu.Nvml]::ClockOffsetInfo(0); $m = [UnlockGpu.Nvml]::ClockOffsetInfo(2)
    "antes: nucleo=$($c[0]) memoria=$($m[0])" | Out-File $o -Encoding utf8
    $r1 = [UnlockGpu.Nvml]::SetClockOffsetMHz(0, $c[0]); $r2 = [UnlockGpu.Nvml]::SetClockOffsetMHz(2, $m[0])
    "set nucleo rc=$r1 $([UnlockGpu.Nvml]::Err($r1)) | set memoria rc=$r2 $([UnlockGpu.Nvml]::Err($r2))" | Out-File $o -Append -Encoding utf8
    $c = [UnlockGpu.Nvml]::ClockOffsetInfo(0); $m = [UnlockGpu.Nvml]::ClockOffsetInfo(2)
    "depois: nucleo=$($c[0]) memoria=$($m[0])" | Out-File $o -Append -Encoding utf8
} catch { "erro: $($_.Exception.Message)" | Out-File $o -Append -Encoding utf8 }

