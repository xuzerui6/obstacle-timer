#define MyAppVersion "0.4.1"
[Setup]
AppId={{AF1EF518-75A5-4A64-A41A-15B41565016D}
AppName=Obstacle Timer
AppVersion={#MyAppVersion}
AppPublisher=Obstacle Timer
DefaultDirName={localappdata}\Programs\ObstacleTimer
DefaultGroupName=Obstacle Timer
OutputDir=..\artifacts
OutputBaseFilename=ObstacleTimer-Setup-0.4.1-win-x64
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
UninstallDisplayIcon={app}\ObstacleTimer.exe
CloseApplications=yes

[Files]
Source: "..\artifacts\windows\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Obstacle Timer"; Filename: "{app}\ObstacleTimer.exe"
Name: "{autodesktop}\Obstacle Timer"; Filename: "{app}\ObstacleTimer.exe"

[Run]
Filename: "{app}\ObstacleTimer.exe"; Description: "Launch Obstacle Timer"; Flags: nowait postinstall skipifsilent
