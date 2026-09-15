; FluxLab Windows installer (Inno Setup). Adapted from VariLab/TransitLab's proven config.
;
; Per-user install (no admin/UAC) into {localappdata}\FluxLab. This is separate from the app's
; own user-data folder, %AppData%\SimpleFitsViewer (Roaming) -- saved camera profiles and the
; last-used settings live there and are NEVER touched by install or uninstall. (The app keeps the
; internal name SimpleFitsViewer for that folder so existing profiles carry over; only the shipped
; executable is branded FluxLab.exe.)
;
; AppId is a fixed GUID so future versions upgrade in place rather than installing side-by-side.
;
; This is an additional distribution option alongside the portable win-x64 zip, not a replacement
; -- both are built from the same publish\win-x64 output.
;
; Build: requires publish\win-x64\ to already exist (dotnet publish -c Release -r win-x64
; --self-contained true -o publish\win-x64), then run from the installer\ directory:
;   "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" FluxLab.iss
;
; MyAppVersion is NOT read from AppVersion.cs automatically -- bump it here by hand alongside
; AppVersion.Version on every release.

#define MyAppName "FluxLab"
#define MyAppVersion "2.2.2"
#define MyAppPublisher "Art Trail"
#define MyAppURL "https://github.com/ArtTrail/FluxLab"
#define MyAppExeName "FluxLab.exe"

[Setup]
AppId={{F7DB5966-4504-4459-A6F6-C8E3EABB0582}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={localappdata}\{#MyAppName}
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
LicenseFile=..\..\LICENSE
OutputDir=..\publish
OutputBaseFilename=FluxLab-Setup-v{#MyAppVersion}
SetupIconFile=..\SimpleFitsViewer\Assets\FluxLab.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
