# Unlock GPU - monitor e ajustes da GPU NVIDIA de notebook (NVML publica): telemetria, offsets, trava da VRAM, gravacao de sessoes.
# Leitura pela NVML (nvml.dll, API oficial da NVIDIA). Trava pela nvidia-smi (-lmc / -rmc).
# Uso:
#   UnlockGPU.ps1            abre a janela (pede administrador)
#   UnlockGPU.ps1 -Lock      trava sem janela no nivel escolhido por ultimo (tarefa de inicializacao)
#   UnlockGPU.ps1 -Unlock    destrava sem janela
#   UnlockGPU.ps1 -SelfTest  so le e confere, nao altera nada
param(
    [switch]$Lock,
    [switch]$Unlock,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$AppName    = 'Unlock GPU'
$AppVersion = '3.3'
$TaskName   = 'UnlockGPU - travar VRAM ao entrar no Windows'
$ScriptPath = $MyInvocation.MyCommand.Path
$ScriptDir  = Split-Path -Parent $ScriptPath
$DataDir    = Join-Path $env:LOCALAPPDATA 'UnlockGPU'
$StateFile  = Join-Path $DataDir 'estado.json'
$PrefFile   = Join-Path $DataDir 'preferencias.json'
$LogFile    = Join-Path $DataDir 'log.txt'
$SessionDir = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'UnlockGPU\sessoes'

# Migra os dados da versao com o nome antigo (Trava VRAM), uma vez.
$OldDataDir = Join-Path $env:LOCALAPPDATA 'TravaVRAM'
if ((Test-Path $OldDataDir) -and -not (Test-Path $DataDir)) {
    try { Copy-Item -Path $OldDataDir -Destination $DataDir -Recurse -ErrorAction Stop } catch { }
}
$AssetsDir = @((Join-Path $ScriptDir 'assets'), (Join-Path (Split-Path -Parent $ScriptDir) 'assets')) | Where-Object { Test-Path $_ } | Select-Object -First 1

function Write-AppLog([string]$msg) {
    try {
        if (-not (Test-Path $DataDir)) { New-Item -ItemType Directory -Force $DataDir | Out-Null }
        Add-Content -Path $LogFile -Value ("{0}  {1}" -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $msg) -Encoding UTF8
    } catch { }
}

# ---------- NVML (leitura) ----------
$script:NvmlOk = $false
try {
    if (-not ('UnlockGpu.Nvml' -as [type])) {
        Add-Type -TypeDefinition (Get-Content (Join-Path $ScriptDir 'Nvml.cs') -Raw) -Language CSharp
    }
    $script:NvmlOk = [UnlockGpu.Nvml]::Init()
} catch { Write-AppLog "NVML indisponivel: $($_.Exception.Message)" }

# ---------- nvidia-smi (trava) ----------
function Get-NvidiaSmi {
    $p = Join-Path $env:SystemRoot 'System32\nvidia-smi.exe'
    if (Test-Path $p) { return $p }
    $c = Get-Command nvidia-smi.exe -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    return $null
}
$Smi = Get-NvidiaSmi

function Invoke-Smi([string[]]$SmiArgs) {
    if (-not $Smi -or -not (Test-Path -LiteralPath $Smi)) { return @{ Code = -1; Out = 'nvidia-smi não encontrado' } }
    $ErrorActionPreference = 'Continue'
    try {
        $out = & $Smi @SmiArgs 2>&1 | ForEach-Object { "$_" } | Out-String
        return @{ Code = $LASTEXITCODE; Out = $out.Trim() }
    } catch {
        return @{ Code = -1; Out = $_.Exception.Message }
    }
}

function Format-MHz($n) { return ([int]$n).ToString('N0', [Globalization.CultureInfo]'pt-BR') }
function Format-W($w) { if ([double]::IsNaN([double]$w)) { return '?' }; return ([double]$w).ToString('N0', [Globalization.CultureInfo]'pt-BR') }

# Niveis travaveis: os dois clocks de memoria mais altos (P0 maximo e P2 intermediario).
function Get-Levels {
    $vals = @()
    if ($script:NvmlOk) { $vals = @([UnlockGpu.Nvml]::SupportedMemClocks() | ForEach-Object { [int]$_ }) }
    if (-not $vals) {
        $r = Invoke-Smi @('--query-supported-clocks=memory', '--format=csv,noheader,nounits')
        if ($r.Code -eq 0) { $vals = @($r.Out -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^\d+$' } | ForEach-Object { [int]$_ }) }
    }
    return @($vals | Sort-Object -Descending -Unique | Select-Object -First 2)
}

function Get-Sample {
    # Sempre tenta: se a NVML falhou ao abrir (driver reiniciando), ela se recupera sozinha depois.
    if (-not ('UnlockGpu.Nvml' -as [type])) { return $null }
    try { $s = [UnlockGpu.Nvml]::Sample() } catch { return $null }
    if (-not $s.Ok) { return $null }
    if (-not $script:NvmlOk) {
        $script:NvmlOk = $true
        try {
            $script:Limits = [UnlockGpu.Nvml]::PowerLimits()
            $script:HeaderBase = $null; Set-Header $s.PState
            if (-not $script:Levels) { $script:Levels = @(Get-Levels) }
        } catch { }
    }
    return $s
}

$script:BootId = $null
function Get-BootId {
    if ($script:BootId) { return $script:BootId }
    try { $script:BootId = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToString('s') } catch { $script:BootId = '' }
    return $script:BootId
}

function Save-State([bool]$locked, $clock, [string]$source = 'app') {
    if (-not (Test-Path $DataDir)) { New-Item -ItemType Directory -Force $DataDir | Out-Null }
    @{ locked = $locked; clock = $clock; source = $source; boot = (Get-BootId); when = (Get-Date).ToString('s') } |
        ConvertTo-Json | Set-Content -Path $StateFile -Encoding UTF8
}

function Get-State {
    try {
        if (-not (Test-Path $StateFile)) { return $null }
        $s = Get-Content $StateFile -Raw | ConvertFrom-Json
        if ($s.boot -ne (Get-BootId)) { return $null }
        return $s
    } catch { return $null }
}

function Get-PrefLevel {
    try { if (Test-Path $PrefFile) { return [int](Get-Content $PrefFile -Raw | ConvertFrom-Json).level } } catch { }
    return 0
}
function Save-PrefLevel([int]$level) {
    try {
        if (-not (Test-Path $DataDir)) { New-Item -ItemType Directory -Force $DataDir | Out-Null }
        @{ level = $level } | ConvertTo-Json | Set-Content -Path $PrefFile -Encoding UTF8
    } catch { }
}

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Lock-Vram([int]$target) {
    $levels = Get-Levels
    if (-not $levels) { return @{ Ok = $false; Msg = 'Não consegui ler os clocks de memória da GPU.' } }
    if (-not $target -or ($levels -notcontains $target)) { $target = $levels[0] }
    $r = Invoke-Smi @('-lmc', "$target,$target")
    $ok = ($r.Code -eq 0) -and ($r.Out -match '(?i)set to|All done')
    if ($ok) { Save-State $true $target 'app'; Write-AppLog "LOCK $target MHz ok" }
    else { Write-AppLog "LOCK $target MHz falhou: $($r.Out)" }
    return @{ Ok = $ok; Clock = $target; Msg = $(if ($ok) { "VRAM travada no nível $(Format-MHz $target) MHz." } else { "O driver recusou a trava: $($r.Out)" }) }
}

function Unlock-Vram {
    $r = Invoke-Smi @('-rmc')
    $ok = ($r.Code -eq 0)
    if ($ok) { Save-State $false $null 'app'; Write-AppLog 'UNLOCK ok' }
    else { Write-AppLog "UNLOCK falhou: $($r.Out)" }
    return @{ Ok = $ok; Msg = $(if ($ok) { 'VRAM de volta ao padrão (automático).' } else { "O driver recusou: $($r.Out)" }) }
}

function Get-PStateBase([string]$pstate, [int[]]$levels) {
    if (-not $levels) { return $null }
    if ($pstate -eq 'P0') { return $levels[0] }
    if ($pstate -eq 'P2' -and $levels.Count -ge 2) { return $levels[1] }
    return $null
}

# Detecta a trava pela propria GPU: parada e sem trava ela desce para P4/P5/P8.
# Presa em P0/P2 com uso baixo por 3 leituras seguidas = travada.
$script:IdleLockedSeen = 0
$script:LastDetected = $null
$script:LockedAt = $null
function Get-LockInfo($s, $st, [int[]]$levels) {
    $info = @{ Locked = $false; Base = $null; Offset = $null; Lost = $false; Detected = $false }
    $stLocked = [bool]($st -and $st.locked)
    if ($s -and $s.UtilGpu -lt 15) {
        $base = Get-PStateBase $s.PState $levels
        if ($base) {
            $script:IdleLockedSeen++
            if ($script:IdleLockedSeen -ge 3 -or $stLocked) {
                $script:LastDetected = @{ Base = $base; Offset = ([int]$s.MemMHz - $base) }
                $info.Locked = $true; $info.Base = $base; $info.Offset = [int]$s.MemMHz - $base; $info.Detected = $true
                return $info
            }
            if ($script:LastDetected) { $info.Locked = $true; $info.Base = $script:LastDetected.Base; $info.Offset = $script:LastDetected.Offset }
            return $info
        }
        if ($script:LockedAt -and ((Get-Date) - $script:LockedAt).TotalSeconds -lt 10 -and $script:LastDetected) {
            $info.Locked = $true; $info.Base = $script:LastDetected.Base
            return $info
        }
        $script:IdleLockedSeen = 0
        $script:LastDetected = $null
        $info.Lost = $stLocked
        return $info
    }
    if ($script:LastDetected) { $info.Locked = $true; $info.Base = $script:LastDetected.Base; $info.Offset = $script:LastDetected.Offset; return $info }
    if ($stLocked) { $info.Locked = $true; $info.Base = [int]$st.clock; if ($s) { $info.Offset = [int]$s.MemMHz - [int]$st.clock } }
    return $info
}

function Get-AutoTask {
    try {
        $t = Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop
        if (($t.Actions | ForEach-Object { $_.Arguments }) -join ' ' -like "*$ScriptPath*") { return $t }
        return $null
    } catch { return $null }
}

function Set-AutoTask([bool]$enable) {
    if ($enable) {
        $arg = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$ScriptPath`" -Lock"
        $action = New-ScheduledTaskAction -Execute (Join-Path $PSHOME 'powershell.exe') -Argument $arg
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
        $trigger.Delay = 'PT30S'
        $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Highest
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 3) -StartWhenAvailable
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
            -Description 'Trava o clock da VRAM da GPU NVIDIA no nivel escolhido ao entrar no Windows (criado pelo app Unlock GPU).' -Force | Out-Null
        Write-AppLog 'AUTOSTART ligado'
    } else {
        if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) { Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false }
        Write-AppLog 'AUTOSTART desligado'
    }
}

# ---------- gravacao de sessao ----------
$script:Rec = $null   # @{ File; Start; N; Load = lista de amostras sob carga }
function Start-Recording {
    if (-not (Test-Path $SessionDir)) { New-Item -ItemType Directory -Force $SessionDir | Out-Null }
    $f = Join-Path $SessionDir ("sessao-{0}.csv" -f (Get-Date).ToString('yyyyMMdd-HHmmss'))
    # Um so arquivo aberto com compartilhamento: abrir o CSV no Excel durante a gravacao nao trava nada.
    $fs = New-Object IO.FileStream($f, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
    $wr = New-Object IO.StreamWriter($fs, (New-Object Text.UTF8Encoding($true)))
    $wr.AutoFlush = $true
    $wr.WriteLine('data_hora;nivel;nucleo_mhz;sm_mhz;memoria_mhz;vram_uso_mb;potencia_w;limite_w;temp_c;uso_gpu;uso_mem;motivo')
    $script:Rec = @{ File = $f; Writer = $wr; Start = Get-Date; N = 0; NLoad = 0; SumW = 0.0; SumCore = 0.0; MaxW = 0.0; MaxT = 0; Mem = @{} }
    Write-AppLog "GRAVACAO inicio $f"
}

function Add-RecordingSample($s) {
    if (-not $script:Rec -or -not $s) { return }
    $ci = [Globalization.CultureInfo]'pt-BR'   # virgula decimal: o Excel em portugues le como numero
    $pw = if ([double]::IsNaN($s.PowerW)) { '' } else { $s.PowerW.ToString('0.0', $ci) }
    $lw = if ([double]::IsNaN($s.LimitW)) { '' } else { $s.LimitW.ToString('0', $ci) }
    $line = '{0};{1};{2};{3};{4};{5};{6};{7};{8};{9};{10};{11}' -f $s.Time.ToString('yyyy-MM-dd HH:mm:ss'), $s.PState, $s.CoreMHz, $s.SmMHz, $s.MemMHz, [math]::Round($s.VramUsedMB), $pw, $lw, $s.TempC, $s.UtilGpu, $s.UtilMem, [UnlockGpu.Nvml]::ReasonsText($s.Reasons)
    $script:Rec.Writer.WriteLine($line)
    $script:Rec.N++
    if ($s.UtilGpu -ge 90 -and -not [double]::IsNaN($s.PowerW) -and $s.CoreMHz -gt 0 -and $s.MemMHz -gt 0) {
        $script:Rec.NLoad++; $script:Rec.SumW += $s.PowerW; $script:Rec.SumCore += $s.CoreMHz
        if ($s.PowerW -gt $script:Rec.MaxW) { $script:Rec.MaxW = $s.PowerW }
        if ($s.TempC -gt $script:Rec.MaxT) { $script:Rec.MaxT = $s.TempC }
        $k = [string]$s.MemMHz; $script:Rec.Mem[$k] = 1 + [int]$script:Rec.Mem[$k]
    }
}

function Format-Duration([TimeSpan]$d) {
    if ($d.TotalHours -ge 1) { return '{0}:{1:mm\:ss}' -f [int][math]::Floor($d.TotalHours), $d }
    return '{0:mm\:ss}' -f $d
}

function Get-RecordingSummary {
    if (-not $script:Rec) { return '' }
    $r = $script:Rec
    $txt = "Gravando há {0}  ·  {1} leituras" -f (Format-Duration ((Get-Date) - $r.Start)), $r.N
    if ($r.NLoad -gt 0) {
        $avg = $r.SumW / $r.NLoad
        $core = $r.SumCore / $r.NLoad
        $memTop = ($r.Mem.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { "$(Format-MHz $_.Key) ($([math]::Round(100 * $_.Value / $r.NLoad))%)" }) -join ', '
        $txt += "`nSob carga ($($r.NLoad) leituras): média $(Format-W $avg) W  ·  pico $(Format-W $r.MaxW) W  ·  núcleo $(Format-MHz $core) MHz  ·  máx $($r.MaxT) °C"
        $txt += "`nVRAM: $memTop"
    } else {
        $txt += "`nAinda sem carga (uso da GPU acima de 90%)."
    }
    return $txt
}

function Stop-Recording {
    if (-not $script:Rec) { return $null }
    $sum = (Get-RecordingSummary) -replace '^Gravando há', 'Duração:'
    try { $script:Rec.Writer.Dispose() } catch { }
    $txtFile = [IO.Path]::ChangeExtension($script:Rec.File, '.resumo.txt')
    $sum | Set-Content -Path $txtFile -Encoding UTF8
    Write-AppLog "GRAVACAO fim $($script:Rec.File)"
    $f = $script:Rec.File
    $script:Rec = $null
    return @{ File = $f; Summary = $sum }
}

# ---------- modos sem janela ----------
if ($SelfTest) {
    $lv = Get-Levels
    $s = Get-Sample
    "nvml: $($script:NvmlOk) $(if ($script:NvmlOk) { [UnlockGpu.Nvml]::Name() + ' / ' + [UnlockGpu.Nvml]::Driver() })"
    "nvidia-smi: $Smi"
    "admin: $(Test-Admin)"
    "levels: $($lv -join ', ')"
    "limits: $(if ($script:NvmlOk) { [UnlockGpu.Nvml]::PowerLimits() -join ' / ' })"
    if ($s) { "sample: {0} core={1} mem={2} pow={3:N1} lim={4:N0} T={5} util={6} reasons={7}" -f $s.PState, $s.CoreMHz, $s.MemMHz, $s.PowerW, $s.LimitW, $s.TempC, $s.UtilGpu, [UnlockGpu.Nvml]::ReasonsText($s.Reasons) } else { 'sample: (falhou)' }
    "state: $((Get-State) | ConvertTo-Json -Compress)"
    "autostart task: $([bool](Get-AutoTask))"
}

if ($Lock -or $Unlock) {
    $modo = if ($Lock) { 'LOCK' } else { 'UNLOCK' }
    if (-not (Test-Admin)) { Write-AppLog "$modo sem janela sem administrador: ignorado"; exit 2 }
    $res = $null
    for ($i = 1; $i -le 12; $i++) {
        try { $res = if ($Lock) { Lock-Vram (Get-PrefLevel) } else { Unlock-Vram } } catch { $res = @{ Ok = $false; Msg = $_.Exception.Message } }
        if ($res.Ok) { if ($i -gt 1) { Write-AppLog "$modo ok na tentativa $i" }; exit 0 }
        if ($i -lt 12) { Start-Sleep -Seconds 5 }
    }
    Write-AppLog "$modo desistiu apos 12 tentativas: $($res.Msg)"
    exit 1
}

# ---------- janela ----------
if (-not $SelfTest -and -not (Test-Admin)) {
    try {
        Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -Verb RunAs -WindowStyle Hidden `
            -ArgumentList "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$ScriptPath`"" -ErrorAction Stop
    } catch {
        Add-Type -AssemblyName PresentationFramework
        [void][System.Windows.MessageBox]::Show('O Unlock GPU precisa de permissão de administrador para mudar o clock da memória. Abra de novo e clique em Sim na janela do Windows.', $AppName)
    }
    exit
}

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

[xml]$xaml = @"
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Unlock GPU" Width="820" SizeToContent="Height" ResizeMode="CanMinimize"
        WindowStartupLocation="CenterScreen" Background="#15191E" FontFamily="Segoe UI">
  <Window.Resources>
    <Style TargetType="Button">
      <Setter Property="Foreground" Value="#0B0F12"/>
      <Setter Property="FontSize" Value="14"/>
      <Setter Property="FontWeight" Value="SemiBold"/>
      <Setter Property="Height" Value="42"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="Button">
            <Border x:Name="b" Background="{TemplateBinding Background}" CornerRadius="8">
              <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Opacity" Value="0.85"/></Trigger>
              <Trigger Property="IsEnabled" Value="False"><Setter TargetName="b" Property="Opacity" Value="0.4"/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style TargetType="RadioButton">
      <Setter Property="Foreground" Value="#E8ECEF"/>
      <Setter Property="FontSize" Value="13"/>
      <Setter Property="Margin" Value="0,4,0,0"/>
    </Style>
    <Style x:Key="Card" TargetType="Border">
      <Setter Property="Background" Value="#1E242A"/>
      <Setter Property="CornerRadius" Value="10"/>
      <Setter Property="Padding" Value="14"/>
    </Style>
    <Style x:Key="TileLabel" TargetType="TextBlock">
      <Setter Property="Foreground" Value="#8A949C"/>
      <Setter Property="FontSize" Value="11"/>
    </Style>
    <Style x:Key="TileValue" TargetType="TextBlock">
      <Setter Property="Foreground" Value="#E8ECEF"/>
      <Setter Property="FontSize" Value="22"/>
      <Setter Property="FontWeight" Value="SemiBold"/>
      <Setter Property="Margin" Value="0,2,0,0"/>
    </Style>
    <Style x:Key="TileSub" TargetType="TextBlock">
      <Setter Property="Foreground" Value="#8A949C"/>
      <Setter Property="FontSize" Value="11"/>
    </Style>
  </Window.Resources>
  <StackPanel Margin="20">
    <DockPanel>
      <Image x:Name="Logo" Width="34" Height="34" Margin="0,0,10,0" VerticalAlignment="Center"/>
      <TextBlock Text="Unlock GPU" Foreground="#E8ECEF" FontSize="22" FontWeight="SemiBold" VerticalAlignment="Center"/>
      <TextBlock x:Name="GpuName" Text="" Foreground="#8A949C" FontSize="12" VerticalAlignment="Bottom" Margin="12,0,0,4"/>
    </DockPanel>

    <UniformGrid Columns="4" Margin="0,14,0,0">
      <Border Style="{StaticResource Card}" Margin="0,0,8,8"><StackPanel>
        <TextBlock Text="POTÊNCIA AGORA" Style="{StaticResource TileLabel}"/>
        <TextBlock x:Name="TPower" Text="-" Style="{StaticResource TileValue}"/>
        <TextBlock x:Name="TPowerSub" Text="" Style="{StaticResource TileSub}"/></StackPanel></Border>
      <Border Style="{StaticResource Card}" Margin="0,0,8,8"><StackPanel>
        <TextBlock Text="NÚCLEO" Style="{StaticResource TileLabel}"/>
        <TextBlock x:Name="TCore" Text="-" Style="{StaticResource TileValue}"/>
        <TextBlock Text="MHz" Style="{StaticResource TileSub}"/></StackPanel></Border>
      <Border Style="{StaticResource Card}" Margin="0,0,8,8"><StackPanel>
        <TextBlock Text="SM" Style="{StaticResource TileLabel}"/>
        <TextBlock x:Name="TSm" Text="-" Style="{StaticResource TileValue}"/>
        <TextBlock Text="MHz" Style="{StaticResource TileSub}"/></StackPanel></Border>
      <Border Style="{StaticResource Card}" Margin="0,0,0,8"><StackPanel>
        <TextBlock Text="MEMÓRIA" Style="{StaticResource TileLabel}"/>
        <TextBlock x:Name="TMem" Text="-" Style="{StaticResource TileValue}"/>
        <TextBlock Text="MHz" Style="{StaticResource TileSub}"/></StackPanel></Border>
      <Border Style="{StaticResource Card}" Margin="0,0,8,0"><StackPanel>
        <TextBlock Text="VRAM EM USO" Style="{StaticResource TileLabel}"/>
        <TextBlock x:Name="TVram" Text="-" Style="{StaticResource TileValue}"/>
        <TextBlock x:Name="TVramSub" Text="" Style="{StaticResource TileSub}"/></StackPanel></Border>
      <Border Style="{StaticResource Card}" Margin="0,0,8,0"><StackPanel>
        <TextBlock Text="NÍVEL (P-STATE)" Style="{StaticResource TileLabel}"/>
        <TextBlock x:Name="TPState" Text="-" Style="{StaticResource TileValue}"/>
        <TextBlock x:Name="TPStateSub" Text="" Style="{StaticResource TileSub}"/></StackPanel></Border>
      <Border Style="{StaticResource Card}" Margin="0,0,8,0"><StackPanel>
        <TextBlock Text="TEMPERATURA" Style="{StaticResource TileLabel}"/>
        <TextBlock x:Name="TTemp" Text="-" Style="{StaticResource TileValue}"/>
        <TextBlock Text="°C" Style="{StaticResource TileSub}"/></StackPanel></Border>
      <Border Style="{StaticResource Card}"><StackPanel>
        <TextBlock Text="USO DA GPU" Style="{StaticResource TileLabel}"/>
        <TextBlock x:Name="TUtil" Text="-" Style="{StaticResource TileValue}"/>
        <TextBlock x:Name="TUtilSub" Text="" Style="{StaticResource TileSub}"/></StackPanel></Border>
    </UniformGrid>

    <Border Style="{StaticResource Card}" Margin="0,8,0,0">
      <DockPanel>
        <TextBlock Text="Cortes agora:" Foreground="#8A949C" FontSize="13" VerticalAlignment="Center" DockPanel.Dock="Left"/>
        <WrapPanel x:Name="Chips" Margin="8,0,0,0"/>
      </DockPanel>
    </Border>

    <Border Style="{StaticResource Card}" Margin="0,8,0,0">
      <StackPanel>
        <StackPanel Orientation="Horizontal">
          <TextBlock Text="Últimos 3 minutos (a cada 2 s)" Foreground="#8A949C" FontSize="12"/>
          <Rectangle Width="14" Height="3" Fill="#BAF568" Margin="16,0,6,0" VerticalAlignment="Center"/>
          <TextBlock x:Name="LegPower" Text="Potência" Foreground="#B5BEC5" FontSize="12"/>
          <Rectangle Width="14" Height="3" Fill="#6CB6FF" Margin="16,0,6,0" VerticalAlignment="Center"/>
          <TextBlock x:Name="LegCore" Text="Núcleo" Foreground="#B5BEC5" FontSize="12"/>
          <Rectangle Width="14" Height="3" Fill="#C69CFF" Margin="16,0,6,0" VerticalAlignment="Center"/>
          <TextBlock x:Name="LegMem" Text="VRAM" Foreground="#B5BEC5" FontSize="12"/>
        </StackPanel>
        <Canvas x:Name="Graph" Height="150" Margin="0,10,0,0" ClipToBounds="True" Background="#181D22">
          <Polyline x:Name="LMem" Stroke="#C69CFF" StrokeThickness="1.5"/>
          <Polyline x:Name="LCore" Stroke="#6CB6FF" StrokeThickness="1.5"/>
          <Polyline x:Name="LPower" Stroke="#BAF568" StrokeThickness="2"/>
        </Canvas>
      </StackPanel>
    </Border>

    <Grid Margin="0,8,0,0">
      <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="8"/><ColumnDefinition/></Grid.ColumnDefinitions>

      <Border Grid.Column="0" Style="{StaticResource Card}">
        <StackPanel>
          <TextBlock Text="Trava da VRAM" Foreground="#E8ECEF" FontSize="15" FontWeight="SemiBold"/>
          <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
            <Ellipse x:Name="Dot" Width="11" Height="11" Fill="#6B747C" VerticalAlignment="Center"/>
            <TextBlock x:Name="StatusText" Text="Lendo..." Foreground="#E8ECEF" FontSize="14" Margin="8,0,0,0"/>
          </StackPanel>
          <TextBlock x:Name="LockDetail" Text="" Foreground="#B5BEC5" FontSize="12" TextWrapping="Wrap" Margin="19,4,0,0"/>
          <RadioButton x:Name="RbHigh" GroupName="lvl" Content="Máximo" Margin="0,10,0,0"/>
          <RadioButton x:Name="RbMid" GroupName="lvl" Content="Intermediário"/>
          <Button x:Name="BtnToggle" Content="Travar VRAM" Background="#BAF568" Margin="0,12,0,0"/>
          <CheckBox x:Name="ChkAuto" Margin="0,12,0,0" Foreground="#E8ECEF" FontSize="12">Travar ao entrar no Windows</CheckBox>
          <TextBlock Foreground="#6F7A83" FontSize="11" TextWrapping="Wrap" Margin="0,10,0,0"
            Text="Nos testes, travar a VRAM reduziu a potência da GPU quando ela está no limite de energia. Use em jogo leve ou com FPS limitado."/>
        </StackPanel>
      </Border>

      <Border Grid.Column="2" Style="{StaticResource Card}">
        <StackPanel>
          <TextBlock Text="Gravação de sessão" Foreground="#E8ECEF" FontSize="15" FontWeight="SemiBold"/>
          <TextBlock x:Name="RecText" Text="Grava uma leitura por segundo em CSV e calcula média, pico, clock e VRAM sob carga. Use para comparar perfis no mesmo teste." Foreground="#B5BEC5" FontSize="12" TextWrapping="Wrap" Margin="0,8,0,0"/>
          <Button x:Name="BtnRec" Content="Começar gravação" Background="#6CB6FF" Margin="0,12,0,0"/>
          <Button x:Name="BtnOpen" Content="Abrir pasta das sessões" Background="#9AA6B0" Margin="0,8,0,0"/>
        </StackPanel>
      </Border>
    </Grid>

    <Border Style="{StaticResource Card}" Margin="0,8,0,0">
      <StackPanel>
        <TextBlock Text="Ajustes" Foreground="#E8ECEF" FontSize="15" FontWeight="SemiBold"/>
        <Grid Margin="0,10,0,0">
          <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="16"/><ColumnDefinition/></Grid.ColumnDefinitions>
          <StackPanel Grid.Column="0">
            <TextBlock Text="Offset do núcleo" Foreground="#8A949C" FontSize="12"/>
            <DockPanel Margin="0,4,0,0">
              <TextBlock x:Name="CoreOffVal" DockPanel.Dock="Right" Width="70" TextAlignment="Right" Foreground="#E8ECEF" FontSize="13" FontWeight="SemiBold"/>
              <Slider x:Name="CoreOffSlider" Minimum="-1000" Maximum="1000" TickFrequency="15" IsSnapToTickEnabled="True" SmallChange="15" LargeChange="90"/>
            </DockPanel>
            <TextBlock Text="Offset da memória" Foreground="#8A949C" FontSize="12" Margin="0,10,0,0"/>
            <DockPanel Margin="0,4,0,0">
              <TextBlock x:Name="MemOffVal" DockPanel.Dock="Right" Width="70" TextAlignment="Right" Foreground="#E8ECEF" FontSize="13" FontWeight="SemiBold"/>
              <Slider x:Name="MemOffSlider" Minimum="-2000" Maximum="6000" TickFrequency="15" IsSnapToTickEnabled="True" SmallChange="15" LargeChange="90"/>
            </DockPanel>
            <TextBlock x:Name="OffInfo" Text="" Foreground="#6F7A83" FontSize="11" TextWrapping="Wrap" Margin="0,6,0,0"/>
            <Grid Margin="0,10,0,0">
              <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="8"/><ColumnDefinition/></Grid.ColumnDefinitions>
              <Button x:Name="BtnOffApply" Grid.Column="0" Content="Aplicar offsets" Background="#BAF568"/>
              <Button x:Name="BtnOffZero" Grid.Column="2" Content="Zerar" Background="#9AA6B0"/>
            </Grid>
          </StackPanel>
          <StackPanel Grid.Column="2">
            <TextBlock Text="Limite de potência da GPU" Foreground="#8A949C" FontSize="12"/>
            <TextBlock x:Name="PowerInfo" Text="" Foreground="#E8ECEF" FontSize="13" TextWrapping="Wrap" Margin="0,4,0,0"/>
            <DockPanel x:Name="PowerEdit" Margin="0,6,0,0" Visibility="Collapsed">
              <Button x:Name="BtnPowerApply" DockPanel.Dock="Right" Content="Aplicar" Width="90" Height="32" Background="#BAF568" Margin="8,0,0,0"/>
              <TextBlock x:Name="PowerVal" DockPanel.Dock="Right" Width="56" TextAlignment="Right" Foreground="#E8ECEF" FontSize="13" FontWeight="SemiBold" VerticalAlignment="Center"/>
              <Slider x:Name="PowerSlider" TickFrequency="1" IsSnapToTickEnabled="True" VerticalAlignment="Center"/>
            </DockPanel>
            <TextBlock Text="Teto da placa inteira" Foreground="#8A949C" FontSize="12" Margin="0,14,0,0"/>
            <StackPanel Orientation="Horizontal" Margin="0,4,0,0">
              <TextBlock Text="Carregador (etiqueta):" Foreground="#B5BEC5" FontSize="12" VerticalAlignment="Center"/>
              <TextBox x:Name="AdapterBox" Width="60" Margin="8,0,4,0" Background="#15191E" Foreground="#E8ECEF" BorderBrush="#3A434B" FontSize="13" Padding="4,2"/>
              <TextBlock Text="W" Foreground="#B5BEC5" FontSize="12" VerticalAlignment="Center"/>
            </StackPanel>
            <TextBlock x:Name="BudgetText" Text="" Foreground="#B5BEC5" FontSize="12" TextWrapping="Wrap" Margin="0,6,0,0"/>
          </StackPanel>
        </Grid>
      </StackPanel>
    </Border>

    <Border x:Name="VerifyBox" Style="{StaticResource Card}" Margin="0,8,0,0" Visibility="Collapsed">
      <TextBlock x:Name="VerifyText" Text="" Foreground="#E8ECEF" FontSize="13" TextWrapping="Wrap"/>
    </Border>
    <TextBlock x:Name="MsgText" Text="" Foreground="#BAF568" FontSize="12" TextWrapping="Wrap" Margin="2,10,0,0"/>
  </StackPanel>
</Window>
"@

$reader = New-Object System.Xml.XmlNodeReader $xaml
$win = [Windows.Markup.XamlReader]::Load($reader)
$ui = @{}
foreach ($n in 'Logo','GpuName','TPower','TPowerSub','TCore','TSm','TMem','TVram','TVramSub','TPState','TPStateSub','TTemp','TUtil','TUtilSub','Chips','VerifyBox','VerifyText','Graph','LPower','LCore','LMem','LegPower','LegCore','LegMem','Dot','StatusText','LockDetail','RbHigh','RbMid','BtnToggle','ChkAuto','RecText','BtnRec','BtnOpen','MsgText','CoreOffVal','CoreOffSlider','MemOffVal','MemOffSlider','OffInfo','BtnOffApply','BtnOffZero','PowerInfo','PowerEdit','BtnPowerApply','PowerVal','PowerSlider','AdapterBox','BudgetText') { $ui[$n] = $win.FindName($n) }

if ($SelfTest) { "xaml: ok ($($ui.Keys.Count) controles, nulos: $(@($ui.Keys | Where-Object { -not $ui[$_] }).Count))"; exit 0 }

if ($AssetsDir) {
    try {
        $img = New-Object Windows.Media.Imaging.BitmapImage
        $img.BeginInit(); $img.UriSource = [Uri](Join-Path $AssetsDir 'logo-256.png'); $img.CacheOption = 'OnLoad'; $img.EndInit()
        $ui.Logo.Source = $img
        $win.Icon = [Windows.Media.Imaging.BitmapFrame]::Create([Uri](Join-Path $AssetsDir 'UnlockGPU.ico'))
    } catch { Write-AppLog "logo: $($_.Exception.Message)" }
}

$script:Levels = @(Get-Levels)
$script:Limits = $null
$script:MaxCore = 3000
if ($script:NvmlOk) {
    Set-Header ''
    $script:Limits = [UnlockGpu.Nvml]::PowerLimits()
    $mc = [UnlockGpu.Nvml]::MaxCoreMHz(); if ($mc -gt 0) { $script:MaxCore = [int]$mc }
} else {
    $ui.GpuName.Text = "NVML indisponível: $([UnlockGpu.Nvml]::LastError)"
}
if ($script:Levels.Count -ge 1) { $ui.RbHigh.Content = "Máximo  ·  $(Format-MHz $script:Levels[0]) MHz (P0)" }
if ($script:Levels.Count -ge 2) { $ui.RbMid.Content = "Intermediário  ·  $(Format-MHz $script:Levels[1]) MHz (P2)" } else { $ui.RbMid.Visibility = 'Collapsed' }
$pref = Get-PrefLevel
if ($script:Levels.Count -ge 2 -and $pref -eq $script:Levels[1]) { $ui.RbMid.IsChecked = $true } else { $ui.RbHigh.IsChecked = $true }

function Get-SelectedLevel {
    if ($ui.RbMid.IsChecked -and $script:Levels.Count -ge 2) { return $script:Levels[1] }
    return $script:Levels[0]
}

function Show-Msg([string]$t, [bool]$ok) {
    $ui.MsgText.Text = $t
    $ui.MsgText.Foreground = $(if ($ok) { '#BAF568' } else { '#FF7B72' })
}

# Historico para o grafico: 180 amostras (3 min a 1 por segundo).
$script:Hist = New-Object System.Collections.Generic.List[object]
function Update-Graph {
    $w = $ui.Graph.ActualWidth; $h = $ui.Graph.ActualHeight
    if ($w -le 0 -or $h -le 0) { return }
    $n = 90   # 3 minutos a cada 2 s
    $maxW = 250.0
    if ($script:Limits -and $script:Limits[2] -gt 0) { $maxW = [double]$script:Limits[2] }
    $maxMem = 16000.0
    if ($script:Levels.Count -ge 1) { $maxMem = [double]$script:Levels[0] * 1.25 }
    $pp = New-Object System.Windows.Media.PointCollection($n)
    $pc = New-Object System.Windows.Media.PointCollection($n)
    $pm = New-Object System.Windows.Media.PointCollection($n)
    $start = [math]::Max(0, $script:Hist.Count - $n)
    for ($i = $start; $i -lt $script:Hist.Count; $i++) {
        $s = $script:Hist[$i]
        $x = $w * ($i - $start + ($n - [math]::Min($n, $script:Hist.Count))) / ($n - 1)
        $pw = if ([double]::IsNaN($s.PowerW)) { 0 } else { [math]::Min($s.PowerW, $maxW) }
        $pp.Add([System.Windows.Point]::new($x, ($h - 4 - ($h - 8) * $pw / $maxW)))
        $pc.Add([System.Windows.Point]::new($x, ($h - 4 - ($h - 8) * [math]::Min($s.CoreMHz, $script:MaxCore) / $script:MaxCore)))
        $pm.Add([System.Windows.Point]::new($x, ($h - 4 - ($h - 8) * [math]::Min($s.MemMHz, $maxMem) / $maxMem)))
    }
    $pp.Freeze(); $pc.Freeze(); $pm.Freeze()
    $ui.LPower.Points = $pp; $ui.LCore.Points = $pc; $ui.LMem.Points = $pm
}

$script:IsLocked = $false
$script:LastViewError = ''
function Set-Header([string]$pstate) {
    if (-not $script:HeaderBase) {
        try { $script:HeaderBase = "$([UnlockGpu.Nvml]::Name())  ·  driver $([UnlockGpu.Nvml]::Driver())  ·  vBIOS $([UnlockGpu.Nvml]::Vbios())" } catch { return }
    }
    $ui.GpuName.Text = "$($script:HeaderBase)  ·  $(if ($pstate) { $pstate } else { '?' })  ·  v$AppVersion"
}

# Etiquetas de corte: so redesenha quando a mascara muda.
$script:ChipsMask = [uint64]::MaxValue
function Set-Chips($items) {
    $ui.Chips.Children.Clear()
    foreach ($it in $items) {
        $kind, $text = $it -split '\|', 2
        $bg = switch ($kind) { 'alerta' { '#4A2A1E' } 'ok' { '#1F3A24' } default { '#2A3138' } }
        $fg = switch ($kind) { 'alerta' { '#FFB38A' } 'ok' { '#BAF568' } default { '#B5BEC5' } }
        $b = New-Object System.Windows.Controls.Border
        $b.Background = $bg; $b.CornerRadius = 10; $b.Padding = '10,3,10,3'; $b.Margin = '0,2,6,2'
        $t = New-Object System.Windows.Controls.TextBlock
        $t.Text = $text; $t.Foreground = $fg; $t.FontSize = 12; $t.FontWeight = 'SemiBold'
        $b.Child = $t
        [void]$ui.Chips.Children.Add($b)
    }
}

# Confirmacao por leitura de volta: depois de aplicar, le o hardware ate bater (ou ate 12 s).
$script:Verify = $null   # @{ Kind = 'lock'|'unlock'; Level; Start }
function Start-Verify([string]$kind, $level) {
    $script:Verify = @{ Kind = $kind; Level = $level; Start = Get-Date }
    $ui.VerifyBox.Visibility = 'Visible'
    $ui.VerifyText.Foreground = '#E8ECEF'
    $ui.VerifyText.Text = $(if ($kind -eq 'lock') { "Aplicado: VRAM no nível $(Format-MHz $level) MHz → lendo o hardware..." } else { 'Aplicado: VRAM no padrão → lendo o hardware...' })
}
function Update-Verify($s) {
    $v = $script:Verify
    if (-not $v -or -not $s) { return }
    $age = ((Get-Date) - $v.Start).TotalSeconds
    if ($v.Kind -eq 'lock') {
        $base = Get-PStateBase $s.PState $script:Levels
        if ($base -eq $v.Level) {
            $off = [int]$s.MemMHz - $base
            $extra = if ($off -gt 0) { " (nível $(Format-MHz $base) + offset $(Format-MHz $off))" } else { '' }
            $ui.VerifyText.Text = "Aplicado: VRAM no nível $(Format-MHz $v.Level) MHz → lido $(Format-MHz $s.MemMHz) MHz em $($s.PState)$extra  ✓"
            $ui.VerifyText.Foreground = '#BAF568'; $script:Verify = $null; Write-AppLog "VERIFY lock $($v.Level) ok: $($s.PState) $($s.MemMHz)"
        } elseif ($age -ge 12) {
            $ui.VerifyText.Text = "Aplicado: VRAM no nível $(Format-MHz $v.Level) MHz → lido $(Format-MHz $s.MemMHz) MHz em $($s.PState)  ✗ não bateu"
            $ui.VerifyText.Foreground = '#FF7B72'; $script:Verify = $null; Write-AppLog "VERIFY lock $($v.Level) FALHOU: $($s.PState) $($s.MemMHz)"
        }
    } else {
        if ($s.UtilGpu -lt 15 -and -not (Get-PStateBase $s.PState $script:Levels)) {
            $ui.VerifyText.Text = "Aplicado: VRAM no padrão → lido $(Format-MHz $s.MemMHz) MHz em $($s.PState), descendo sozinha  ✓"
            $ui.VerifyText.Foreground = '#BAF568'; $script:Verify = $null; Write-AppLog "VERIFY unlock ok: $($s.PState) $($s.MemMHz)"
        } elseif ($age -ge 12) {
            if ($s.UtilGpu -ge 15) {
                $ui.VerifyText.Text = "Aplicado: VRAM no padrão → GPU sob carga ($($s.UtilGpu)%), não dá para confirmar agora. Confira com a GPU parada."
                $ui.VerifyText.Foreground = '#F2C14E'
            } else {
                $ui.VerifyText.Text = "Aplicado: VRAM no padrão → lido $(Format-MHz $s.MemMHz) MHz em $($s.PState)  ✗ continua presa"
                $ui.VerifyText.Foreground = '#FF7B72'
            }
            $script:Verify = $null
        }
    }
}

function Update-View {
    try {
        $s = Get-Sample
        if ($s) {
            $script:Hist.Add($s); if ($script:Hist.Count -gt 400) { $script:Hist.RemoveRange(0, 200) }
            try { Add-RecordingSample $s } catch { if ($script:LastViewError -ne "rec:$($_.Exception.Message)") { Write-AppLog "REC: $($_.Exception.Message)"; $script:LastViewError = "rec:$($_.Exception.Message)" } }
            # media dos ultimos 30 s (15 leituras de 2 s)
            $recent = @($script:Hist | Select-Object -Last 15 | Where-Object { -not [double]::IsNaN($_.PowerW) } | ForEach-Object { $_.PowerW })
            $avg = if ($recent.Count) { ($recent | Measure-Object -Average).Average } else { [double]::NaN }
            Set-Header $s.PState
            $ui.TPower.Text = Format-W $s.PowerW
            $ui.TPowerSub.Text = "W  ·  média 30 s $(Format-W $avg)  ·  limite $(Format-W $s.LimitW)"
            $ui.TCore.Text = Format-MHz $s.CoreMHz
            $ui.TSm.Text = Format-MHz $s.SmMHz
            $ui.TMem.Text = Format-MHz $s.MemMHz
            $ui.TVram.Text = ([double]$s.VramUsedMB / 1024).ToString('N1', [Globalization.CultureInfo]'pt-BR')
            $ui.TVramSub.Text = "GB de $(([double]$s.VramTotalMB / 1024).ToString('N1', [Globalization.CultureInfo]'pt-BR'))"
            $ui.TPState.Text = $s.PState
            $ui.TPStateSub.Text = $(switch ($s.PState) { 'P0' { 'máximo' } 'P2' { 'intermediário' } default { 'econômico' } })
            $ui.TTemp.Text = [string]$s.TempC
            $ui.TUtil.Text = "$($s.UtilGpu)%"
            $ui.TUtilSub.Text = "controlador de memória $($s.UtilMem)%"
            if ($s.Reasons -ne $script:ChipsMask) { Set-Chips ([UnlockGpu.Nvml]::ReasonsList($s.Reasons)); $script:ChipsMask = $s.Reasons }
            $ui.LegPower.Text = "Potência $(Format-W $s.PowerW) W"
            $ui.LegCore.Text = "Núcleo $(Format-MHz $s.CoreMHz)"
            $ui.LegMem.Text = "Memória $(Format-MHz $s.MemMHz)"
            Update-Graph
            Update-Verify $s
        } else {
            Set-Chips @('alerta|Não consegui ler a GPU agora'); $script:ChipsMask = [uint64]::MaxValue
        }

        $st = Get-State
        $info = Get-LockInfo $s $st $script:Levels
        if ($info.Lost) {
            Save-State $false $null 'detectado'
            Show-Msg 'A trava da VRAM foi desfeita (reinício do driver ou Apply no NvpwrControl).' $false
            Write-AppLog 'trava perdida detectada'
        }
        if ($info.Detected -and -not ($st -and $st.locked)) { Save-State $true $info.Base 'detectado' }
        $script:IsLocked = [bool]$info.Locked
        if ($info.Locked) {
            $ui.Dot.Fill = '#BAF568'
            $ui.StatusText.Text = $(if ($info.Base) { "Travada no nível $(Format-MHz $info.Base) MHz" } else { 'Travada' })
            $ui.LockDetail.Text = $(if ($info.Base -and $info.Offset -gt 0) { "Nível $(Format-MHz $info.Base) + offset $(Format-MHz $info.Offset) = $(Format-MHz ($info.Base + $info.Offset)) MHz" } else { '' })
            $ui.BtnToggle.Content = 'Destravar (voltar ao padrão)'
            $ui.BtnToggle.Background = '#9AA6B0'
        } else {
            $ui.Dot.Fill = '#6B747C'
            $ui.StatusText.Text = 'Padrão (automático)'
            $ui.LockDetail.Text = ''
            $ui.BtnToggle.Content = "Travar VRAM em $(Format-MHz (Get-SelectedLevel)) MHz"
            $ui.BtnToggle.Background = '#BAF568'
        }
        $ui.RbHigh.IsEnabled = -not $info.Locked
        $ui.RbMid.IsEnabled = -not $info.Locked

        if ($script:Rec) { $ui.RecText.Text = Get-RecordingSummary }
        $script:LastViewError = ''
    } catch {
        $m = $_.Exception.Message
        try { Set-Chips @("alerta|Erro ao atualizar: $m"); $script:ChipsMask = [uint64]::MaxValue } catch { }
        if ($m -ne $script:LastViewError) { Write-AppLog "Update-View: $m"; $script:LastViewError = $m }
    }
}

$ui.BtnToggle.Add_Click({
    try {
        if ($script:IsLocked) {
            $r = Unlock-Vram
            if ($r.Ok) { $script:LastDetected = $null; $script:IdleLockedSeen = 0; $script:LockedAt = $null; Start-Verify 'unlock' $null }
        } else {
            $lvl = Get-SelectedLevel
            Save-PrefLevel $lvl
            $r = Lock-Vram $lvl
            if ($r.Ok) { $script:LastDetected = @{ Base = $r.Clock; Offset = $null }; $script:LockedAt = Get-Date; Start-Verify 'lock' $r.Clock }
        }
        Show-Msg $r.Msg $r.Ok
    } catch { Show-Msg "Erro: $($_.Exception.Message)" $false; Write-AppLog "TOGGLE erro: $($_.Exception.Message)" }
    Update-View
})
$ui.RbHigh.Add_Checked({ if (-not $script:IsLocked) { Save-PrefLevel (Get-SelectedLevel); Update-View } })
$ui.RbMid.Add_Checked({ if (-not $script:IsLocked) { Save-PrefLevel (Get-SelectedLevel); Update-View } })

$ui.BtnRec.Add_Click({
    try {
        if ($script:Rec) {
            $r = Stop-Recording
            $ui.BtnRec.Content = 'Começar gravação'; $ui.BtnRec.Background = '#6CB6FF'
            $ui.RecText.Text = "$($r.Summary)`nSalvo em: $($r.File)"
            Show-Msg 'Gravação salva.' $true
        } else {
            Start-Recording
            $ui.BtnRec.Content = 'Parar e salvar'; $ui.BtnRec.Background = '#FF7B72'
            $ui.RecText.Text = Get-RecordingSummary
        }
    } catch { Show-Msg "Erro na gravação: $($_.Exception.Message)" $false; Write-AppLog "REC erro: $($_.Exception.Message)" }
})
$ui.BtnOpen.Add_Click({
    try {
        if (-not (Test-Path $SessionDir)) { New-Item -ItemType Directory -Force $SessionDir | Out-Null }
        Start-Process explorer.exe -ArgumentList "`"$SessionDir`""
    } catch { Show-Msg "Não consegui abrir a pasta: $($_.Exception.Message)" $false }
})

$script:Loading = $true
$ui.ChkAuto.IsChecked = [bool](Get-AutoTask)
$script:Loading = $false
$ui.ChkAuto.Add_Checked({
    if ($script:Loading) { return }
    try { Set-AutoTask $true; Show-Msg "Vai travar sozinho no nível $(Format-MHz (Get-SelectedLevel)) MHz, 30 s depois de entrar no Windows." $true }
    catch { $script:Loading = $true; $ui.ChkAuto.IsChecked = $false; $script:Loading = $false; Show-Msg "Não consegui criar a tarefa: $($_.Exception.Message)" $false }
})
$ui.ChkAuto.Add_Unchecked({
    if ($script:Loading) { return }
    try { Set-AutoTask $false; Show-Msg 'Inicialização automática desligada.' $true }
    catch { $script:Loading = $true; $ui.ChkAuto.IsChecked = $true; $script:Loading = $false; Show-Msg "Não consegui remover a tarefa: $($_.Exception.Message)" $false }
})

# ---------- ajustes: offsets, limite de potencia, teto da placa ----------
$CapsFile = Join-Path $DataDir 'capacidades.json'
$AdapterFile = Join-Path $DataDir 'carregador.json'
function Format-Off([int]$v) { if ($v -gt 0) { "+$v" } else { "$v" } }

function Show-Verify([string]$text, [string]$color) {
    $script:Verify = $null
    $ui.VerifyBox.Visibility = 'Visible'; $ui.VerifyText.Text = $text; $ui.VerifyText.Foreground = $color
}

function Init-Offsets {
    $c = [UnlockGpu.Nvml]::ClockOffsetInfo(0); $m = [UnlockGpu.Nvml]::ClockOffsetInfo(2)
    if (-not $c -or -not $m) {
        $ui.CoreOffSlider.IsEnabled = $false; $ui.MemOffSlider.IsEnabled = $false; $ui.BtnOffApply.IsEnabled = $false; $ui.BtnOffZero.IsEnabled = $false
        $ui.OffInfo.Text = 'Este driver não informa offsets de clock pela NVML.'
        return
    }
    $ui.CoreOffSlider.Minimum = $c[1]; $ui.CoreOffSlider.Maximum = $c[2]; $ui.CoreOffSlider.Value = $c[0]
    $ui.MemOffSlider.Minimum = $m[1]; $ui.MemOffSlider.Maximum = $m[2]; $ui.MemOffSlider.Value = $m[0]
    $ui.OffInfo.Text = "Faixa do driver: núcleo $(Format-Off $c[1]) a $(Format-Off $c[2]), memória $(Format-Off $m[1]) a $(Format-Off $m[2]) MHz. Em vigor agora: núcleo $(Format-Off $c[0]), memória $(Format-Off $m[0]). Some ao reiniciar o driver."
    $ui.CoreOffVal.Text = "$(Format-Off ([int]$ui.CoreOffSlider.Value)) MHz"; $ui.MemOffVal.Text = "$(Format-Off ([int]$ui.MemOffSlider.Value)) MHz"
}

function Apply-Offsets([int]$core, [int]$mem) {
    $rc1 = [UnlockGpu.Nvml]::SetClockOffsetMHz(0, $core)
    $rc2 = [UnlockGpu.Nvml]::SetClockOffsetMHz(2, $mem)
    $c = [UnlockGpu.Nvml]::ClockOffsetInfo(0); $m = [UnlockGpu.Nvml]::ClockOffsetInfo(2)
    $rc = if ($c) { $c[0] } else { $null }; $rm = if ($m) { $m[0] } else { $null }
    $ok = ($rc1 -eq 0 -and $rc2 -eq 0 -and $rc -eq $core -and $rm -eq $mem)
    $txt = "Aplicado: núcleo $(Format-Off $core), memória $(Format-Off $mem) MHz → lido núcleo $(if ($rc -ne $null) { Format-Off $rc } else { '?' }), memória $(if ($rm -ne $null) { Format-Off $rm } else { '?' }) MHz"
    if ($ok) { $txt += '  ✓' } else {
        $txt += '  ✗ não bateu'
        if ($rc1 -ne 0) { $txt += " (núcleo: $([UnlockGpu.Nvml]::Err($rc1)))" }
        if ($rc2 -ne 0) { $txt += " (memória: $([UnlockGpu.Nvml]::Err($rc2)))" }
    }
    Show-Verify $txt $(if ($ok) { '#BAF568' } else { '#FF7B72' })
    Write-AppLog "OFFSETS pedido $core/$mem rc=$rc1/$rc2 lido $rc/$rm"
    Init-Offsets
}

# O limite pela NVML so funciona em alguns notebooks. Testa uma vez por driver, reaplicando o valor em vigor (nao muda nada).
function Get-PowerSetSupport {
    $drv = [UnlockGpu.Nvml]::Driver()
    try { if (Test-Path $CapsFile) { $c = Get-Content $CapsFile -Raw | ConvertFrom-Json; if ($c.driver -eq $drv) { return [int]$c.powerSetRc } } } catch { }
    $enf = [UnlockGpu.Nvml]::EnforcedLimitW()
    $rc = [UnlockGpu.Nvml]::SetPowerLimitW($enf)
    # So guarda resposta definitiva (0 = aceita, 3 = nao suportado); sem permissao etc. testa de novo na proxima.
    if ($rc -eq 0 -or $rc -eq 3) { try { @{ driver = $drv; powerSetRc = $rc } | ConvertTo-Json | Set-Content -Path $CapsFile -Encoding UTF8 } catch { } }
    Write-AppLog "CAPS limite de potencia via NVML: rc=$rc ($([UnlockGpu.Nvml]::Err($rc)))"
    return $rc
}

function Init-Power {
    $lim = $script:Limits; $enf = [UnlockGpu.Nvml]::EnforcedLimitW()
    if (-not $lim) { $ui.PowerInfo.Text = 'Não consegui ler os limites.'; return }
    $rc = Get-PowerSetSupport
    $base = "Em vigor $(Format-W $enf) W  ·  faixa do driver $(Format-W $lim[0])–$(Format-W $lim[2]) W"
    if ($rc -eq 0) {
        $ui.PowerEdit.Visibility = 'Visible'
        $ui.PowerSlider.Minimum = $lim[0]; $ui.PowerSlider.Maximum = $lim[2]; $ui.PowerSlider.Value = $enf
        $ui.PowerVal.Text = "$(Format-W $ui.PowerSlider.Value) W"
        $ui.PowerInfo.Text = $base
    } else {
        $ui.PowerEdit.Visibility = 'Collapsed'
        $ui.PowerInfo.Text = "$base`nEste notebook não aceita mudar o limite pela NVML ($([UnlockGpu.Nvml]::Err($rc))): quem decide é o firmware do fabricante. O ajuste por marca vem nos modos do fabricante."
    }
}

function Update-Budget {
    $enf = [UnlockGpu.Nvml]::EnforcedLimitW()
    $a = 0; [void][int]::TryParse($ui.AdapterBox.Text, [ref]$a)
    if ($a -le 0) { $ui.BudgetText.Text = 'Digite a potência impressa na etiqueta do carregador.'; return }
    $rest = $a - $enf
    $ui.BudgetText.Text = "GPU até $(Format-W $enf) W  +  CPU e resto até $(Format-W $rest) W  =  $a W do carregador."
    $ui.BudgetText.Foreground = $(if ($rest -lt 60) { '#FF7B72' } else { '#B5BEC5' })
    if ($rest -lt 60) { $ui.BudgetText.Text += "`nSobra pouco para a CPU: com CPU e GPU no máximo juntas, o carregador não dá conta." }
    try { @{ watts = $a } | ConvertTo-Json | Set-Content -Path $AdapterFile -Encoding UTF8 } catch { }
}

$ui.CoreOffSlider.Add_ValueChanged({ $ui.CoreOffVal.Text = "$(Format-Off ([int]$ui.CoreOffSlider.Value)) MHz" })
$ui.MemOffSlider.Add_ValueChanged({ $ui.MemOffVal.Text = "$(Format-Off ([int]$ui.MemOffSlider.Value)) MHz" })
$ui.PowerSlider.Add_ValueChanged({ $ui.PowerVal.Text = "$(Format-W $ui.PowerSlider.Value) W" })
$ui.BtnOffApply.Add_Click({ try { Apply-Offsets ([int]$ui.CoreOffSlider.Value) ([int]$ui.MemOffSlider.Value) } catch { Show-Msg "Erro: $($_.Exception.Message)" $false } })
$ui.BtnOffZero.Add_Click({ try { $ui.CoreOffSlider.Value = 0; $ui.MemOffSlider.Value = 0; Apply-Offsets 0 0 } catch { Show-Msg "Erro: $($_.Exception.Message)" $false } })
$ui.BtnPowerApply.Add_Click({
    try {
        $want = [math]::Round($ui.PowerSlider.Value)
        $rc = [UnlockGpu.Nvml]::SetPowerLimitW($want)
        Start-Sleep -Milliseconds 300
        $got = [UnlockGpu.Nvml]::EnforcedLimitW()
        $ok = ($rc -eq 0 -and [math]::Abs($got - $want) -lt 1)
        Show-Verify ("Aplicado $want W → lido $(Format-W $got) W  " + $(if ($ok) { '✓' } else { "✗ não bateu ($([UnlockGpu.Nvml]::Err($rc)))" })) $(if ($ok) { '#BAF568' } else { '#FF7B72' })
        Write-AppLog "POWER pedido $want rc=$rc lido $got"
        Init-Power; Update-Budget
    } catch { Show-Msg "Erro: $($_.Exception.Message)" $false }
})
$ui.AdapterBox.Add_TextChanged({ try { Update-Budget } catch { } })

if ($script:NvmlOk) {
    try { Init-Offsets } catch { Write-AppLog "Init-Offsets: $($_.Exception.Message)" }
    try { Init-Power } catch { Write-AppLog "Init-Power: $($_.Exception.Message)" }
}
try { if (Test-Path $AdapterFile) { $ui.AdapterBox.Text = [string](Get-Content $AdapterFile -Raw | ConvertFrom-Json).watts } else { Update-Budget } } catch { }

$timer = New-Object System.Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromSeconds(2)
$timer.Add_Tick({ Update-View })
$win.Add_ContentRendered({ Update-View; $timer.Start() })
$win.Add_Closing({
    try { if ($script:Rec) { [void](Stop-Recording) } } catch { }
})
$win.Add_Closed({ $timer.Stop(); try { [UnlockGpu.Nvml]::Shutdown() } catch { } })
$win.Dispatcher.Add_UnhandledException({ param($s, $e) Write-AppLog "ERRO UI: $($e.Exception.Message)"; $e.Handled = $true })

try { [void]$win.ShowDialog() } catch { Write-AppLog "ERRO janela: $($_.Exception.Message)" }
