; VocalB1 installer script (Inno Setup 6)
;
; Build it through scripts/package-release.ps1 -Installer, which passes:
;   /DMyAppVersion=<VERSION>
;   /DStageDir=<folder holding VocalB1.exe and the engine files>
;   /DOutDir=<dist folder>
;
; Manual build:
;   ISCC.exe /DMyAppVersion=1.0.0 /DStageDir=..\dist\_stage\vocalb1-1.0.0-win-x64 vocalb1.iss
;
; The file is intentionally pure ASCII: Inno Setup reads a BOM-less .iss as ANSI,
; the same trap that mangles a BOM-less UTF-8 .ps1 under PowerShell 5.1.

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef StageDir
  #define StageDir "..\dist\_stage"
#endif
#ifndef OutDir
  #define OutDir "..\dist"
#endif

#define MyAppName "VocalB1"
#define MyAppShortName "VocalB1"
#define MyAppPublisher "Xichun Guo"
#define MyAppURL "https://vocalb1.com"
#define MyAppExeName "VocalB1.exe"
#define MyAppIcon "VocalB1_Version_1.0.ico"

[Setup]
; Keep this AppId forever: it is how Windows recognises an upgrade of the same product.
AppId={{3F2B7A4E-9C1D-4B77-9E2A-5D6C8B1F0A31}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoCompany={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppShortName}
DefaultGroupName={#MyAppShortName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutDir}
OutputBaseFilename=vocalb1-{#MyAppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppIcon}
SetupIconFile=..\{#MyAppIcon}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
LicenseFile=..\LICENSE
AllowNoIcons=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Everything the release script staged: VocalB1.exe, the engine, vocal_lib,
; languages, ui\bridge and an empty voicebank folder with its README.
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\{#MyAppIcon}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppIcon}"; IconIndex: 0
Name: "{group}\Voicebank folder"; Filename: "explorer.exe"; Parameters: """{app}\voicebank"""; IconFilename: "shell32.dll"; IconIndex: 3
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppIcon}"; IconIndex: 0; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Only build artefacts; user projects (saves.db) and voicebanks are kept on purpose.
Type: filesandordirs; Name: "{app}\cache"
Type: filesandordirs; Name: "{app}\out"
