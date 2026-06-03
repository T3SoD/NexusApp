#define AppName    "Nexus"
#define AppVersion "4.0.0"
#define ExeName    "Nexus_v4.exe"
#define PublishDir "Nexus_v4\bin\x64\Release\net8.0-windows10.0.17763.0\win-x64\publish"

[Setup]
AppId={{F7A2C8D5-3E91-4B6F-A012-7C5E3D8B9F04}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=TurboV1RG1N
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
