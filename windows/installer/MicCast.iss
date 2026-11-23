; MicCast Windows 安装包（Inno Setup 6）
; 由 tools/build-installer.ps1 调用，SourceDir / AppVersion 由命令行传入：
;   ISCC.exe /DAppVersion=1.0.0 /DSourceDir=..\..\dist\publish MicCast.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\dist\publish"
#endif

#define AppName "MicCast"
#define AppPublisher "MicCast"
#define AppExe "MicCastReceiver.exe"

[Setup]
AppId={{8C1F2A64-5B3D-4E77-9A21-6D4F0C8B7E13}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppSupportURL=https://github.com/q269384828/MicCast
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; 装到当前用户目录：程序会把 last-ip.txt / winui-crash.log 写在自身目录，
; 放 Program Files 会因为没权限而静默失败，导致「记住上次 IP」失效。
PrivilegesRequired=lowest
OutputDir=..\..\dist
OutputBaseFilename=MicCast-Setup-{#AppVersion}-win-x64
SetupIconFile=..\MicCastReceiver\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 与 csproj 的 TargetPlatformMinVersion 对齐
MinVersion=10.0.17763
LicenseFile=..\..\LICENSE

[Languages]
Name: "chinese"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; 托盘菜单「把 CABLE Output 设为默认麦克风」会从程序目录往上找这个脚本
Source: "..\Set-DefaultMic.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 运行期生成的文件
Type: files; Name: "{app}\last-ip.txt"
Type: files; Name: "{app}\winui-crash.log"

[Code]
// 卸载时顺手清掉开机自启项（程序菜单里勾选过就会写 HKCU Run）
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MicCastReceiver');
end;
