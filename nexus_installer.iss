#define AppName    "Nexus"
#define ExeName    "NexusApp.exe"
; PublishDir can be overridden from the command line (CI passes /DPublishDir=publish_out).
#ifndef PublishDir
  #define PublishDir "NexusApp\bin\x64\Release\net8.0-windows10.0.17763.0\win-x64\publish"
#endif
; Version is single-sourced: by default it's read from the built exe's file
; version (which comes from the csproj <Version>). CI can still override with a
; clean tag value via /DAppVersion=X.Y.Z.
#ifndef AppVersion
  #define AppVersion GetFileVersion(AddBackslash(PublishDir) + ExeName)
#endif

[Setup]
AppId={{F7A2C8D5-3E91-4B6F-A012-7C5E3D8B9F04}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=T3SoD
DefaultDirName={localappdata}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir={#SourcePath}\..
OutputBaseFilename=Nexus_Setup
Compression=lzma
SolidCompression=yes
PrivilegesRequired=lowest
CloseApplications=yes
UninstallDisplayName={#AppName}
; Teal app icon for the "Installed apps" / uninstall entry (Inno does not set this
; automatically — without it Windows shows a generic icon). Points at the icon we
; ship below, not the exe, so it's a clean teal regardless of exe embedding.
UninstallDisplayIcon={app}\nexus.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"

[InstallDelete]
; A self-contained publish names its files after the assembly. Builds up to 5.0.0
; shipped as Nexus_v4.*; on upgrade, remove those so the app folder is left with
; only the renamed NexusApp.* files (no stale Nexus_v4.exe to launch by mistake).
Type: files; Name: "{app}\Nexus_v4.*"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Ship the teal app icon so the shortcuts + uninstall entry can reference a real
; .ico file (the <ApplicationIcon> is embedded in the exe, not copied loose).
Source: "NexusApp\Assets\nexus.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; IconFilename pins the teal .ico explicitly. Besides guaranteeing teal, pointing
; the shortcut at a new icon path makes Windows load it fresh instead of reusing a
; cached gold icon from a previous install.
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#ExeName}"; IconFilename: "{app}\nexus.ico"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#ExeName}"; IconFilename: "{app}\nexus.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#ExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
