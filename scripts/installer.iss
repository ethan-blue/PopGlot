#ifndef MyAppVersion
  #error MyAppVersion must be provided with /DMyAppVersion=<version>
#endif

#define MyAppPublisherURL "https://github.com/ethan-blue/PopGlot"
#define MyAppSupportURL "https://github.com/ethan-blue/PopGlot/issues"
#define MyAppUpdatesURL "https://github.com/ethan-blue/PopGlot/releases/latest"

[Setup]
; 升级识别的唯一身份，永不更改。
AppId={{9A6C4E1B-7D23-4F58-B0A1-C3E9D2751F80}
AppName=PopGlot
AppVersion={#MyAppVersion}
AppPublisher=PopGlot
AppPublisherURL={#MyAppPublisherURL}
AppSupportURL={#MyAppSupportURL}
AppUpdatesURL={#MyAppUpdatesURL}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany=PopGlot
SourceDir=..
DefaultDirName={autopf}\PopGlot
DefaultGroupName=PopGlot
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UsePreviousAppDir=yes
DisableDirPage=auto
DirExistsWarning=no
UpdateUninstallLogAppName=yes
Uninstallable=yes
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=yes
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
WizardResizable=yes
LanguageDetectionMethod=none
AllowNoIcons=yes
AlwaysRestart=no
OutputDir=dist
OutputBaseFilename=PopGlot-v{#MyAppVersion}-win-x64-setup
SetupIconFile=apps\PopGlot.Windows\Assets\PopGlot-v5.ico
UninstallDisplayName=PopGlot {#MyAppVersion}
UninstallDisplayIcon={app}\PopGlot.exe
#ifdef POPGLOT_SIGNTOOL
SignTool=popglotsign $f
SignedUninstaller=yes
#endif

; 中文界面依赖 Inno Setup 自带的 Languages\ChineseSimplified.isl，而该语言文件在
; 部分安装形态（例如某些 CI 镜像预装的版本）里并不存在。build-installer.ps1
; 会在编译前探测：缺失时定义 POPGLOT_NO_ZH_LANG，这里退化为纯英文界面，
; 编译照常完成——绝不因为少一个语言文件就让整个安装包出不来。
[Languages]
#ifdef POPGLOT_NO_ZH_LANG
Name: "english"; MessagesFile: "compiler:Default.isl"
#else
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
#endif

[Tasks]
Name: "startmenuicon"; Description: "创建开始菜单快捷方式"; GroupDescription: "快捷方式："
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
Source: "dist\release\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\PopGlot"; Filename: "{app}\PopGlot.exe"; Tasks: startmenuicon
Name: "{group}\卸载 PopGlot"; Filename: "{uninstallexe}"; Tasks: startmenuicon
Name: "{autodesktop}\PopGlot"; Filename: "{app}\PopGlot.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\PopGlot.exe"; Description: "运行 PopGlot"; Flags: nowait postinstall skipifsilent unchecked

[UninstallDelete]
; 用户配置与历史位于 %LOCALAPPDATA%\PopGlot，卸载时刻意保留。

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usDone then
    MsgBox('你的配置与历史保留在 %LOCALAPPDATA%\PopGlot，不会被卸载删除。', mbInformation, MB_OK);
end;
