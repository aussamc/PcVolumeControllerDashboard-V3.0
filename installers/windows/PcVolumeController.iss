; Inno Setup script for the PC Volume Controller Dashboard (Avalonia host).
;
; Packages the self-contained win-x64 publish folder into a signed-later setup .exe:
;   installs to Program Files, adds Start-menu (+ optional desktop) shortcuts, and a
;   proper uninstaller. Built by .github/workflows/build-installer.yml, which passes
;   the version and absolute paths as /D defines so the script has no repo-relative
;   assumptions.
;
; Required defines (all passed on the ISCC command line):
;   MyAppVersion  e.g. 3.14.2
;   SourceDir     absolute path to the published app folder (the ISCC input)
;   OutputDir     absolute path the setup .exe is written to
;   AppIcon       absolute path to app-icon.ico (installer icon + shortcut fallback)

#define MyAppName "PC Volume Controller Dashboard"
#define MyAppExeName "PcVolumeControllerDashboard.Avalonia.exe"
#define MyAppPublisher "aussamc"
#define MyAppURL "https://github.com/aussamc/PcVolumeControllerDashboard-V3.0"

[Setup]
; Stable AppId — never change it, so upgrades replace the prior install in place.
AppId={{9D4FD8B6-4F0D-4B0D-AE35-640A21B431E7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\PC Volume Controller Dashboard
DefaultGroupName=PC Volume Controller Dashboard
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
OutputDir={#OutputDir}
OutputBaseFilename=PcVolumeControllerDashboard-Setup-{#MyAppVersion}
SetupIconFile={#AppIcon}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; win-x64 self-contained build → 64-bit only.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The entire self-contained publish folder (exe + runtime + assets).
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
