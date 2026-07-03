# tools/installer-smoketest.ps1
# インストーラーのスモークテスト: サイレント導入 → 配置検証 → サイレント削除 → 削除検証
# ローカルと CI(release.yml)の両方から同じ検証を実行する
# 注意: 実ユーザープロファイルへ導入→削除するため、インストーラー版 yEdit を常用している
# 環境で実行すると常用インストールが消える(設定 %APPDATA%\yEdit は無傷)
param(
    [Parameter(Mandatory = $true)][string]$SetupPath
)
$ErrorActionPreference = 'Stop'
# CI ログで日本語が文字化けしないよう標準出力を UTF-8 にする(失敗しても検証は続行)
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

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
$openWithKey = 'HKCU:\Software\Classes\.txt\OpenWithProgids'
if (-not (Test-Path $openWithKey)) { throw ".txt の OpenWithProgids キーが無い: $openWithKey" }
$openWith = Get-Item $openWithKey
if ($openWith.GetValueNames() -notcontains 'yEdit.Document') { throw '.txt の OpenWithProgids に yEdit.Document が無い' }

Write-Host 'サイレントアンインストール'
$p = Start-Process -FilePath (Join-Path $appDir 'unins000.exe') -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "アンインストーラーが終了コード $($p.ExitCode) で失敗" }
# unins000.exe は自身のコピーに処理を引き継いで先に返るため、完了をポーリングで待つ
$deadline = (Get-Date).AddSeconds(30)
while ((Test-Path $exe) -and ((Get-Date) -lt $deadline)) { Start-Sleep -Milliseconds 500 }

if (Test-Path $exe)    { throw 'アンインストール後も yEdit.exe が残っている' }
if (Test-Path $sendTo) { throw '「送る」ショートカットが残っている' }
$openWithAfter = Get-Item $openWithKey -ErrorAction SilentlyContinue
if ($openWithAfter -and ($openWithAfter.GetValueNames() -contains 'yEdit.Document')) { throw 'OpenWithProgids の登録が残っている' }

Write-Host 'スモークテスト OK'
