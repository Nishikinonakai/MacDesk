; MacDesk 自安装程序（Inno Setup 6）。
; CI 构建：ISCC /DMyAppVersion=x.y.z /DSourceDir=<publish 目录> installer\macdesk.iss
; 设计要点：
;  - 单用户安装（PrivilegesRequired=lowest，无 UAC）→ %LOCALAPPDATA%\Programs\MacDesk
;  - 升级时 PrepareToInstall 先对已装副本 --quit（优雅退出 = 还原原生桌面图标再换文件）；
;    全新安装但 MacDesk 正从别处运行的场景（开发机）需自行先退
;  - 卸载先 --quit 还原原生图标，并清自启注册（Run 键 / 计划任务）
;  - 用户数据（%LOCALAPPDATA%\MacDesk 布局/设置/备份）永不删除

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{7F4C9AB6-612A-4DEB-92B0-E95D3A7F5321}
AppName=MacDesk
AppVersion={#MyAppVersion}
AppVerName=MacDesk {#MyAppVersion}
AppPublisher=Nishikinonakai
AppPublisherURL=https://github.com/Nishikinonakai/MacDesk
AppSupportURL=https://github.com/Nishikinonakai/MacDesk/issues
DefaultDirName={localappdata}\Programs\MacDesk
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir=.
OutputBaseFilename=MacDesk-Setup-v{#MyAppVersion}
SetupIconFile=..\Assets\macdesk.ico
UninstallDisplayIcon={app}\MacDesk.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
VersionInfoVersion={#MyAppVersion}
CloseApplications=no

[Languages]
; ChineseSimplified 在源码库已转正但 CI 的 Inno 6.7.1 安装包不带 → 随仓库 vendored
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\MacDesk"; Filename: "{app}\MacDesk.exe"; Parameters: "--hide-native"

[Run]
Filename: "{app}\MacDesk.exe"; Parameters: "--hide-native"; Description: "{cm:LaunchProgram,MacDesk}"; Flags: nowait postinstall skipifsilent
; 应用内一键更新走 /VERYSILENT /RELAUNCH=1：静默换完文件自动拉起新版本
Filename: "{app}\MacDesk.exe"; Parameters: "--hide-native"; Flags: nowait; Check: ShouldRelaunch

[Code]
function ShouldRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

// 升级路径：先让已装副本优雅退出（--quit 会向运行中的实例发退出信号，
// 该实例还原原生桌面图标、停看门狗后退出）
function PrepareToInstall(var NeedsRestart: Boolean): String;
var R: Integer;
begin
  if FileExists(ExpandConstant('{app}\MacDesk.exe')) then
  begin
    Exec(ExpandConstant('{app}\MacDesk.exe'), '--quit', '', SW_HIDE, ewWaitUntilTerminated, R);
    Sleep(3000);
  end;
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var R: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    if FileExists(ExpandConstant('{app}\MacDesk.exe')) then
    begin
      Exec(ExpandConstant('{app}\MacDesk.exe'), '--quit', '', SW_HIDE, ewWaitUntilTerminated, R);
      Sleep(4000);
    end;
    // 自启注册指向 {app}，卸载后会悬空——一并清掉（两种机制都清）
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MacDesk');
    Exec('schtasks.exe', '/Delete /TN MacDesk /F', '', SW_HIDE, ewWaitUntilTerminated, R);
  end;
end;
