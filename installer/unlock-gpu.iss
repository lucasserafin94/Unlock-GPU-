; ---------------------------------------------------------------------------
; Unlock GPU - script do Inno Setup
;
; Build local:   ISCC /DAppVersion=4.1.0 installer\unlock-gpu.iss
; CI (release):  o workflow publica em dist\portable e chama o ISCC com a
;                versao vinda da tag (ver .github/workflows/release.yml).
;
; O app nao grava nada na pasta de instalacao em tempo de execucao: dados em
; %LOCALAPPDATA%\UnlockGPU, sessoes em Documentos\UnlockGPU\sessions.
; ---------------------------------------------------------------------------

#define AppName "Unlock GPU"
#ifndef AppVersion
  #define AppVersion "4.1.0"
#endif
#define ExeName "UnlockGPU.exe"
; Pasta com o app publicado (exe + config\romoverride-capture.txt).
#define PublishDir "..\dist\portable"

[Setup]
AppId={{B7E3C2A4-9F1D-4E58-A6C0-2D4F8B9E1A35}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Unlock GPU
DefaultDirName={autopf}\Unlock GPU
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=UnlockGPU-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#ExeName}
SetupIconFile=..\assets\UnlockGPU.ico
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Additional tasks:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#ExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#ExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#ExeName}"; Description: "Launch {#AppName} now"; Flags: nowait postinstall skipifsilent
