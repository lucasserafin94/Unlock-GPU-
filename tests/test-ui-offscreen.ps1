param($p)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$ScriptDir=Split-Path $p; Add-Type -TypeDefinition (Get-Content (Join-Path $ScriptDir 'Nvml.cs') -Raw) -Language CSharp
$src=Get-Content $p -Raw
$ast=[Management.Automation.Language.Parser]::ParseFile($p,[ref]$null,[ref]$null)
foreach($f in $ast.FindAll({$args[0] -is [Management.Automation.Language.FunctionDefinitionAst]},$true)){ . ([scriptblock]::Create($f.Extent.Text)) }
$out='G:\TravaVRAM\tests\out'; $DataDir="$out\data"; $StateFile="$DataDir\estado.json"; $PrefFile="$DataDir\pref.json"; $LogFile="$DataDir\log.txt"; $SessionDir="$out\sessoes"; $TaskName='x'; $ScriptPath=$p; $AppVersion='test'
$script:NvmlOk=[UnlockGpu.Nvml]::Init(); $script:BootId='T'; $script:IdleLockedSeen=0; $script:LastDetected=$null; $script:LockedAt=$null; $script:Rec=$null; $script:LastViewError=''; $script:HeaderBase=$null; $script:ChipsMask=[uint64]::MaxValue; $script:Verify=$null
$Smi='C:\Windows\System32\nvidia-smi.exe'
$xamlText=([regex]::Match($src,'(?s)\[xml\]\$xaml = @"\r?\n(.*?)\r?\n"@')).Groups[1].Value
$win=[Windows.Markup.XamlReader]::Load((New-Object System.Xml.XmlNodeReader ([xml]$xamlText)))
$names=[regex]::Match($src,"foreach \(\`$n in ('[^)]*')\)").Groups[1].Value -split "'\s*,\s*'" | % { $_.Trim("'") }
$ui=@{}; foreach($n in $names){ $ui[$n]=$win.FindName($n) }; "controls: $($names.Count) null: $(@($names|?{-not $ui[$_]}) -join ',')"
$script:Levels=@(Get-Levels); $script:Limits=[UnlockGpu.Nvml]::PowerLimits(); $script:MaxCore=[int][UnlockGpu.Nvml]::MaxCoreMHz(); $ui.RbHigh.IsChecked=$true
$script:Hist=New-Object System.Collections.Generic.List[object]
$ui.Graph.Width=780; $ui.Graph.Measure([Windows.Size]::new(780,150)); $ui.Graph.Arrange([Windows.Rect]::new(0,0,780,150))
Start-Recording
for($i=0;$i -lt 4;$i++){ Update-View; Start-Sleep -Milliseconds 200 }
"header: $($ui.GpuName.Text)"
"tiles: pow={0} [{1}] core={2} sm={3} mem={4} vram={5} {6} ps={7} T={8} util={9} [{10}]" -f $ui.TPower.Text,$ui.TPowerSub.Text,$ui.TCore.Text,$ui.TSm.Text,$ui.TMem.Text,$ui.TVram.Text,$ui.TVramSub.Text,$ui.TPState.Text,$ui.TTemp.Text,$ui.TUtil.Text,$ui.TUtilSub.Text
"chips: $(@($ui.Chips.Children | % { $_.Child.Text }) -join ' | ')"
Set-Chips ([UnlockGpu.Nvml]::ReasonsList(0x404)); "chips(0x404): $(@($ui.Chips.Children | % { $_.Child.Text + '(' + $_.Child.Foreground + ')' }) -join ' | ')"
"graph points: $($ui.LPower.Points.Count)"
# verify logic with synthetic samples
function fake($ps,$mem,$util){ $s=New-Object UnlockGpu.GpuSample; $s.PState=$ps; $s.MemMHz=$mem; $s.UtilGpu=$util; $s.Ok=$true; $s }
Start-Verify 'lock' 14001; Update-Verify (fake 'P4' 9001 2); "lock pending: $($ui.VerifyText.Text)"
Update-Verify (fake 'P0' 15001 2); "lock ok: $($ui.VerifyText.Text) [$($ui.VerifyText.Foreground)]"
Start-Verify 'lock' 11001; $script:Verify.Start=(Get-Date).AddSeconds(-13); Update-Verify (fake 'P4' 9001 2); "lock fail: $($ui.VerifyText.Text) [$($ui.VerifyText.Foreground)]"
Start-Verify 'unlock' $null; Update-Verify (fake 'P4' 9001 3); "unlock ok: $($ui.VerifyText.Text)"
Start-Verify 'unlock' $null; $script:Verify.Start=(Get-Date).AddSeconds(-13); Update-Verify (fake 'P0' 14001 99); "unlock load: $($ui.VerifyText.Text)"
$r=Stop-Recording; Get-Content $r.File | Select -First 2
"log errors:"; if(Test-Path $LogFile){ Get-Content $LogFile | Select-String 'ERRO|Update-View|REC:' }
$CapsFile="$out\caps.json"; $AdapterFile="$out\adapter.json"; Init-Offsets; "offsets: $($ui.OffInfo.Text) | core=$($ui.CoreOffVal.Text) mem=$($ui.MemOffVal.Text) | range mem $($ui.MemOffSlider.Minimum)..$($ui.MemOffSlider.Maximum)"; $ui.AdapterBox.Text='330'; Update-Budget; "budget330: $($ui.BudgetText.Text)"; $ui.AdapterBox.Text='420'; Update-Budget; "budget420: $($ui.BudgetText.Text)"
[UnlockGpu.Nvml]::Shutdown()


