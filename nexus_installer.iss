#define AppName    "Nexus"
#define ExeName    "Nexus_v4.exe"
; PublishDir can be overridden from the command line (CI passes /DPublishDir=publish_out).
#ifndef PublishDir
  #define PublishDir "Nexus_v4\bin\x64\Release\net8.0-windows10.0.17763.0\win-x64\publish"
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

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#ExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#ExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#ExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
