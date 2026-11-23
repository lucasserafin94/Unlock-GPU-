$ErrorActionPreference='Stop'
$o='G:\TravaVRAM\tests\out\setlimit.txt'
try {
  Add-Type -TypeDefinition (Get-Content 'G:\TravaVRAM\src\Nvml.cs' -Raw) -Language CSharp
  [void][UnlockGpu.Nvml]::Init()
  $lim=[UnlockGpu.Nvml]::PowerLimits()
  $cfg=[UnlockGpu.Nvml]::ConfiguredLimitW(); $enf=[UnlockGpu.Nvml]::EnforcedLimitW()
  "antes: configurado=$cfg em_vigor=$enf faixa=$($lim -join '/')" | Out-File $o -Encoding utf8
  $rc=[UnlockGpu.Nvml]::SetPowerLimitW($enf)
  "set($enf W) rc=$rc $([UnlockGpu.Nvml]::Err($rc))" | Out-File $o -Append -Encoding utf8
  Start-Sleep -Milliseconds 500
  "depois: configurado=$([UnlockGpu.Nvml]::ConfiguredLimitW()) em_vigor=$([UnlockGpu.Nvml]::EnforcedLimitW())" | Out-File $o -Append -Encoding utf8
} catch { "erro: $($_.Exception.Message)" | Out-File $o -Append -Encoding utf8 }


