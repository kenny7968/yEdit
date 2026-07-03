# yEdit Windows インストーラー実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Inno Setup 6 によるユーザー単位・UAC不要の日本語インストーラーを作り、リリースCIで zip と並んで GitHub Releases に添付する。

**Architecture:** `installer/yEdit.iss`(Inno Setup スクリプト)がフレームワーク依存 publish 出力を梱包。検証は `tools/installer-smoketest.ps1`(サイレント導入→配置検証→サイレント削除→削除検証)に一本化し、ローカルと CI の両方から同じスクリプトを呼ぶ。設計書: `docs/plans/2026-07-03-installer-design.md`

**Tech Stack:** Inno Setup 6(ISCC.exe / Pascal スクリプト)、PowerShell、GitHub Actions(windows-latest、ISCC プリインストール済み)

**確定値:**
- AppId GUID: `00E3EB19-4DF6-486D-AA76-270ED856E294`(固定。変更するとアップグレードが別アプリ扱いになる)
- リポジトリ URL: `https://github.com/kenny7968/yEdit`
- インストール先: `%LOCALAPPDATA%\Programs\yEdit`({autopf} が lowest 権限でここに解決される)
- 成果物名: `yEdit-v<version>-setup.exe`(CI の `yEdit-${{ github.ref_name }}-setup.exe` と一致させる)

---

### Task 1: Inno Setup をローカルに導入

**Files:** なし(環境準備)

**Step 1: winget でインストール**

```powershell
winget install -e --id JRSoftware.InnoSetup --accept-source-agreements --accept-package-agreements
```

Expected: 正常終了(既に入っていれば「既にインストールされています」でも可)

**Step 2: ISCC.exe の存在確認**

```powershell
Test-Path "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
```

Expected: `True`。False の場合は `Get-ChildItem "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"` も確認(ユーザー単位で入った場合)。以降のタスクでは見つかった方のパスを使う。

---

### Task 2: スモークテストスクリプトを先に書く(失敗を確認)

**Files:**
- Create: `tools/installer-smoketest.ps1`

**Step 1: スクリプト作成**

```powershell
# tools/installer-smoketest.ps1
# インストーラーのスモークテスト: サイレント導入 → 配置検証 → サイレント削除 → 削除検証
# ローカルと CI(release.yml)の両方から同じ検証を実行する
param(
    [Parameter(Mandatory = $true)][string]$SetupPath
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $SetupPath)) { throw "セットアップが見つからない: $SetupPath" }

$appDir   = Join-Path $env:LOCALAPPDATA 'Programs\yEdit'
$exe      = Join-Path $appDir 'yEdit.exe'
$sendTo   = Join-Path $env:APPDATA 'Microsoft\Windows\SendTo\yEdit.lnk'
$startLnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\yEdit.lnk'

Write-Host "サイレントインストール: $SetupPath"
$p = Start-Process -FilePath $SetupPath -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "インストーラーが終了コード $($p.ExitCode) で失敗" }

if (-not (Test-Path $exe))      { throw "インストール後に $exe が存在しない" }
if (-not (Test-Path $sendTo))   { throw "「送る」ショートカットが無い: $sendTo" }
if (-not (Test-Path $startLnk)) { throw "スタートメニューショートカットが無い: $startLnk" }
$openWith = Get-Item 'HKCU:\Software\Classes\.txt\OpenWithProgids'
if ($openWith.GetValueNames() -notcontains 'yEdit.Document') { throw '.txt の OpenWithProgids に yEdit.Document が無い' }

Write-Host 'サイレントアンインストール'
$p = Start-Process -FilePath (Join-Path $appDir 'unins000.exe') -ArgumentList '/VERYSILENT','/NORESTART' -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "アンインストーラーが終了コード $($p.ExitCode) で失敗" }
# unins000.exe は自身のコピーに処理を引き継いで先に返るため、完了をポーリングで待つ
$deadline = (Get-Date).AddSeconds(30)
while ((Test-Path $exe) -and ((Get-Date) -lt $deadline)) { Start-Sleep -Milliseconds 500 }

if (Test-Path $exe)    { throw 'アンインストール後も yEdit.exe が残っている' }
if (Test-Path $sendTo) { throw '「送る」ショートカットが残っている' }
$openWithAfter = Get-Item 'HKCU:\Software\Classes\.txt\OpenWithProgids' -ErrorAction SilentlyContinue
if ($openWithAfter -and ($openWithAfter.GetValueNames() -contains 'yEdit.Document')) { throw 'OpenWithProgids の登録が残っている' }

Write-Host 'スモークテスト OK'
```

注意: デスクトップショートカットは利用者の実デスクトップを汚すため検証対象にしない(手動確認に回す)。

**Step 2: 失敗することを確認(セットアップ未生成)**

```powershell
powershell -File tools\installer-smoketest.ps1 -SetupPath installer\Output\yEdit-v0.0.1-setup.exe
```

Expected: `セットアップが見つからない: ...` で FAIL(exit 1)

**Step 3: コミット**

```powershell
git add tools/installer-smoketest.ps1
git commit -m "test: インストーラーのスモークテストスクリプトを追加(サイレント導入/削除の検証)"
```

---

### Task 3: Inno Setup スクリプト作成とコンパイル

**Files:**
- Create: `installer/yEdit.iss`
- Modify: `.gitignore`(末尾に `publish/` と `installer/Output/` を追加)

**Step 1: publish 出力をローカル生成(インストーラーの梱包対象)**

```powershell
dotnet publish src/yEdit.App -c Release -r win-x64 --self-contained false -p:Version=0.0.1 -p:DebugType=embedded -o publish
```

Expected: `publish\yEdit.exe` が生成される(CI の配布ビルドと同一オプション)

**Step 2: installer/yEdit.iss 作成**

```iss
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
```

**Step 3: .gitignore に生成物を追加**

`.gitignore` の `# Build output` セクション末尾に追記:

```
publish/
installer/Output/
```

**Step 4: コンパイルして成功を確認**

```powershell
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" "/DAppVersion=0.0.1" installer\yEdit.iss
```

Expected: `Successful compile` で `installer\Output\yEdit-v0.0.1-setup.exe` が生成される

**Step 5: コミット**

```powershell
git add installer/yEdit.iss .gitignore
git commit -m "feat: Inno Setup によるインストーラースクリプトを追加(ユーザー単位・ランタイム検出誘導)"
```

---

### Task 4: ローカルスモークテスト(Task 2 のスクリプトが通ることを確認)

**Files:** なし(検証のみ)

**Step 1: スモークテスト実行**

```powershell
powershell -File tools\installer-smoketest.ps1 -SetupPath installer\Output\yEdit-v0.0.1-setup.exe
```

Expected: `スモークテスト OK`(途中で throw したら .iss を修正して Task 3 Step 4 から再実行)

**Step 2: ユーザーデータが無傷なことを確認**

```powershell
Test-Path "$env:APPDATA\yEdit"
```

Expected: インストール/アンインストール前と同じ値(設定があるなら `True` のまま。アンインストールで消えていないこと)

---

### Task 5: ランタイム未導入ダイアログの手動確認

**Files:** なし(検証のみ)

**Step 1: 検出を強制的に失敗させたビルドを作成**

```powershell
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" "/DAppVersion=0.0.1" "/DForceRuntimeMissing" installer\yEdit.iss
```

**Step 2: ウィザードを起動して確認(ユーザーに依頼)**

`installer\Output\yEdit-v0.0.1-setup.exe` を実行し、以下を確認してもらう:

- 起動直後に「.NET 9 デスクトップランタイムが見つかりませんでした…」のダイアログが出る
- 「はい」でブラウザにダウンロードページが開き、ウィザードは続行できる
- ウィザードの各画面が SR(PC-Talker / NVDA)で読める(申し送り事項の消化。すぐできない場合は申し送りのままで先へ進む)
- 確認後はキャンセルで終了してよい

**Step 3: 通常ビルドに戻す**

```powershell
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" "/DAppVersion=0.0.1" installer\yEdit.iss
```

---

### Task 6: リリース CI にインストーラーを統合

**Files:**
- Modify: `.github/workflows/release.yml`

**Step 1: env に SETUP を追加**

```yaml
env:
  TAG: ${{ github.ref_name }}
  ZIP: yEdit-${{ github.ref_name }}-win-x64.zip
  SETUP: yEdit-${{ github.ref_name }}-setup.exe
```

**Step 2: 「zip 圧縮」ステップの直後にインストーラー作成とスモークテストを追加**

```yaml
      - name: インストーラー作成 (Inno Setup)
        shell: pwsh
        env:
          VERSION: ${{ steps.ver.outputs.version }}
        run: >
          & "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
          "/DAppVersion=$env:VERSION"
          "/DPublishDir=$env:GITHUB_WORKSPACE\publish"
          "/O$env:GITHUB_WORKSPACE"
          installer\yEdit.iss

      - name: インストーラーのスモークテスト(サイレント導入/削除)
        shell: pwsh
        run: powershell -File tools\installer-smoketest.ps1 -SetupPath $env:SETUP
```

注: `/O` で出力先をリポジトリルートにし、`$env:SETUP` の名前と一致させる(.iss の `OutputBaseFilename=yEdit-v{#AppVersion}-setup` × VERSION=タグの v なし → `yEdit-v1.2.3-setup.exe`)。

**Step 3: リリースノートの SHA256 を両ファイルに拡張**

「リリースノート組み立て」ステップの該当行を変更:

```powershell
$zipHash = (Get-FileHash $env:ZIP -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()
$setupHash = (Get-FileHash $env:SETUP -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()
```

フッターの SHA256 ブロックを:

```powershell
'**SHA256**'
'```'
"$zipHash  $env:ZIP"
"$setupHash  $env:SETUP"
'```'
```

動作要件の直前に配布形態の説明を 1 行追加:

```powershell
'- setup.exe: インストーラー版(推奨)。zip: 展開して使うポータブル版'
```

**Step 4: リリース作成に setup.exe を添付**

```yaml
        run: >
          gh release create "$env:TAG" "$env:ZIP" "$env:SETUP"
          --title "yEdit $env:TAG"
          --notes-file notes.md
          --verify-tag
```

**Step 5: YAML 構文確認**

```powershell
dotnet build src/yEdit.App -c Release --nologo -v q
```

は不要(CI 変更のみ)。代わりに YAML をパースできることを確認:

```powershell
powershell -Command "Install-Module powershell-yaml -Scope CurrentUser -Force; ConvertFrom-Yaml (Get-Content .github/workflows/release.yml -Raw)"
```

モジュール導入が重ければ目視レビューで代替可(actionlint があればそれでも良い)。

**Step 6: コミット**

```powershell
git add .github/workflows/release.yml
git commit -m "CI: リリースにインストーラー(setup.exe)を追加、スモークテストをゲートに"
```

---

### Task 7: 最終検証とレビュー準備

**Step 1: テストスイートが緑のまま確認**

```powershell
dotnet test tests/yEdit.Core.Tests -c Release
```

Expected: 全件 PASS(280 前後)

**Step 2: 差分の全体確認**

```powershell
git log --oneline main..HEAD
git diff main --stat
```

Expected: 設計書・スモークテスト・.iss・.gitignore・release.yml のみ

**Step 3: マージ前レビュー**

@superpowers:requesting-code-review — 別エージェントにコードレビューを依頼(ユーザーのワークフロー既定)。指摘対応後、superpowers:finishing-a-development-branch で main への no-ff マージへ。

---

## 申し送り(この計画のスコープ外)

- 実タグ push 時の CI 全経路の実地確認(次回リリースで確認)
- コード署名なしによる SmartScreen 警告は既知の制約
- README / ヘルプへのインストール手順記載(ヘルプ整備タスクと合流)
