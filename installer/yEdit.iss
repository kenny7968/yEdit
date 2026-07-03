; yEdit インストーラー (Inno Setup 6)
; コンパイル例:
;   ISCC.exe "/DAppVersion=1.2.3" "/DPublishDir=..\publish" installer\yEdit.iss
; ランタイム未導入ダイアログの手動確認用:
;   ISCC.exe "/DAppVersion=0.0.1" "/DForceRuntimeMissing" installer\yEdit.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

[Setup]
; AppId は固定(変更するとアップグレードが別アプリ扱いになる)
AppId={{00E3EB19-4DF6-486D-AA76-270ED856E294}
AppName=yEdit
AppVersion={#AppVersion}
AppPublisher=kenny7968
AppSupportURL=https://github.com/kenny7968/yEdit
; lowest 権限では {autopf} は %LOCALAPPDATA%\Programs に解決される
DefaultDirName={autopf}\yEdit
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=yEdit-v{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\yEdit.exe
ChangesAssociations=yes
CloseApplications=yes
ShowLanguageDialog=no

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "desktopicon"; Description: "デスクトップにショートカットを作成(&D)"; GroupDescription: "追加のショートカット:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{userprograms}\yEdit"; Filename: "{app}\yEdit.exe"
Name: "{userdesktop}\yEdit"; Filename: "{app}\yEdit.exe"; Tasks: desktopicon
Name: "{usersendto}\yEdit"; Filename: "{app}\yEdit.exe"

[Registry]
; 「プログラムから開く」候補への登録のみ(既定アプリは奪わない)
Root: HKCU; Subkey: "Software\Classes\yEdit.Document"; ValueType: string; ValueData: "yEdit ドキュメント"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\yEdit.Document\DefaultIcon"; ValueType: string; ValueData: "{app}\yEdit.exe,0"
Root: HKCU; Subkey: "Software\Classes\yEdit.Document\shell\open\command"; ValueType: string; ValueData: """{app}\yEdit.exe"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\.txt\OpenWithProgids"; ValueType: string; ValueName: "yEdit.Document"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\Classes\.md\OpenWithProgids"; ValueType: string; ValueName: "yEdit.Document"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\Classes\.csv\OpenWithProgids"; ValueType: string; ValueName: "yEdit.Document"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty

[Code]
const
  DotNetDownloadUrl = 'https://dotnet.microsoft.com/ja-jp/download/dotnet/9.0';

function IsDotNet9DesktopInstalled(): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  FindRec: TFindRec;
begin
#ifdef ForceRuntimeMissing
  Result := False;
  Exit;
#endif
  Result := False;
  { 公式ランタイムインストーラーが書くレジストリ(32bit ビュー)を確認 }
  if RegGetValueNames(HKLM32,
      'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App',
      Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Copy(Names[I], 1, 2) = '9.' then
      begin
        Result := True;
        Exit;
      end;
  { フォールバック: 既定のインストール先フォルダー(SDK 導入などレジストリに出ない場合) }
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\9.*'), FindRec) then
  begin
    FindClose(FindRec);
    Result := True;
  end;
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if WizardSilent then
    Exit;
  if not IsDotNet9DesktopInstalled() then
    if MsgBox('.NET 9 デスクトップランタイムが見つかりませんでした。' + #13#10 +
              'yEdit の実行には .NET 9 デスクトップランタイム (x64) が必要です。' + #13#10 + #13#10 +
              'ダウンロードページを開きますか?' + #13#10 +
              '(インストール自体はこのまま続行できます)',
              mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', DotNetDownloadUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;
