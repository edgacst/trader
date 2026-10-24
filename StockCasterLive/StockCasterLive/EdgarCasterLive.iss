#define MyAppName "EdgarCaster Live"
#define MyAppVersion "1.1.3"
#define MyAppExeName "EdgarCasterLive.exe"

[Setup]
AppId={{7F0F4A0E-CA79-4B41-8F2B-6CF35D11B101}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=에드가씨에스트
DefaultDirName={localappdata}\Programs\EdgarCaster Live
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\EdgarCasterLive.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
OutputDir=dist
OutputBaseFilename=EdgarCasterLive-Setup-1.1.3
SetupIconFile=icon.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
VersionInfoVersion=1.1.3.0

[Tasks]
Name: "desktopicon"; Description: "바탕화면 바로가기 만들기"; GroupDescription: "추가 바로가기:"; Flags: unchecked

[Files]
Source: "publish\win-x64\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\EdgarCaster Live"; Filename: "{app}\EdgarCasterLive.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\EdgarCaster Live"; Filename: "{app}\EdgarCasterLive.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\EdgarCasterLive.exe"; Description: "EdgarCaster Live 실행"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent