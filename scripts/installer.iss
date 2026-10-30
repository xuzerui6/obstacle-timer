#define MyAppVersion "0.5.0"
#ifndef AppSource
  #define AppSource "..\artifacts\windows-0.5.0"
#endif
[Setup]
AppId={{AF1EF518-75A5-4A64-A41A-15B41565016D}
AppName=Obstacle Timer
AppVersion={#MyAppVersion}
AppPublisher=Obstacle Timer
DefaultDirName={localappdata}\Programs\ObstacleTimer
DefaultGroupName=Obstacle Timer
OutputDir=..\artifacts
OutputBaseFilename=ObstacleTimer-Setup-0.5.0-win-x64
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
UninstallDisplayIcon={app}\ObstacleTimer.exe
CloseApplications=no
RestartApplications=no
AppMutex=Local\ObstacleTimer.Desktop.Running

[Files]
Source: "{#AppSource}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Obstacle Timer"; Filename: "{app}\ObstacleTimer.exe"
Name: "{autodesktop}\Obstacle Timer"; Filename: "{app}\ObstacleTimer.exe"

[Run]
Filename: "{app}\ObstacleTimer.exe"; Description: "Launch Obstacle Timer"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
var Locator, Services, Processes: Variant;
begin
  Result := False;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Services := Locator.ConnectServer('', 'root\CIMV2');
    Processes := Services.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''ObstacleTimer.exe'' OR (Name = ''dotnet.exe'' AND CommandLine LIKE ''%ObstacleTimer.dll%'')');
    if Processes.Count > 0 then begin
      MsgBox('计时软件仍在运行。请等比赛、录像和补传空闲后主动退出，再重新运行安装包。安装程序不会关闭正在比赛的实例。', mbInformation, MB_OK);
      Exit;
    end;
    Result := True;
  except
    MsgBox('无法检查正在运行的计时程序。请关闭软件并在空闲时重试安装。', mbError, MB_OK);
  end;
end;

function BackupTree(Source, Target: String): Boolean;
var Entry: TFindRec; FromPath, ToPath: String;
begin
  if Pos(Lowercase(AddBackslash(Source)), Lowercase(AddBackslash(Target))) = 1 then begin
    Result := False;
    Exit;
  end;
  Result := ForceDirectories(Target);
  if not Result then Exit;
  if FindFirst(AddBackslash(Source) + '*', Entry) then begin
    try
      repeat
        if (Entry.Name <> '.') and (Entry.Name <> '..') then begin
          FromPath := AddBackslash(Source) + Entry.Name;
          ToPath := AddBackslash(Target) + Entry.Name;
          if (Entry.Attributes and $400) <> 0 then begin
            Result := False;
            Exit;
          end;
          if (Entry.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
            Result := BackupTree(FromPath, ToPath)
          else
            Result := CopyFile(FromPath, ToPath, False);
          if not Result then Exit;
        end;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var BackupPath: String;
begin
  Result := '';
  if FileExists(ExpandConstant('{app}\ObstacleTimer.exe')) then begin
    BackupPath := ExpandConstant('{localappdata}\ObstacleTimer\release-backups\') + GetDateTimeString('yyyymmdd-hhnnss', '', '');
    if not BackupTree(ExpandConstant('{app}'), BackupPath) then
      Result := '无法创建旧程序回滚备份，升级已停止。请检查磁盘空间与访问权限。';
  end;
end;
