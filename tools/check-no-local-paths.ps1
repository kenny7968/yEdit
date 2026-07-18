# ローカルパスの混入を検出する健全性チェック。
# 対象は Windows / Git Bash / 混合スラッシュを網羅する 2 系統(いずれも
# case-insensitive):
#   - Windows ユーザーホーム下(現ユーザー名込み)  -> %USERPROFILE%\ に置換
#   - 本リポジトリのローカル絶対パス              -> <repo>          に置換
#
# 具体的な variant 例(いずれも検出対象):
#   C:\Users\<username>\   C:/Users/<username>/   /c/Users/<username>/   (大文字小文字問わず)
#   X:\src\yEdit     X:/src/yEdit     /x/src/yEdit     (同上)
#
# このスクリプト自身は例示や regex に literal を含むため、走査対象から
# 除外している($selfPath 参照)。
#
# 用途:
#   pre-commit (Husky) : -Staged で `git diff --cached` 対象のみ検査
#   CI                 : 引数なしで tracked files 全体を検査
#
# text 拡張子ホワイトリストでフィルタしてバイナリは触らない。
# 違反があれば file:line: content を列挙して exit 1。
[CmdletBinding()]
param(
    [switch]$Staged
)
$ErrorActionPreference = 'Stop'

# drive prefix (C:, D:, /c, /d ...) + separator (\ or /) + 対象コンポーネント。
# case-insensitive で 6 variant(3 形式 x Users\<username> or src\yEdit)を包摂。
$patterns = @(
    '(?i)([a-z]:|/[a-z])[\\/]Users[\\/]<username>\b',
    '(?i)([a-z]:|/[a-z])[\\/]src[\\/]yEdit\b'
)

$selfPath = 'tools/check-no-local-paths.ps1'

$textExtensions = @(
    '.md', '.txt', '.cs', '.csproj', '.props', '.targets', '.sln',
    '.json', '.yml', '.yaml', '.xml', '.config',
    '.ps1', '.psm1', '.psd1', '.sh', '.bat', '.cmd',
    '.iss', '.editorconfig', '.gitattributes', '.gitignore',
    '.resx', '.settings'
)

if ($Staged) {
    $files = git -c core.quotepath=false diff --cached --name-only --diff-filter=ACM | Where-Object { $_ -and $_.Trim() }
} else {
    $files = git -c core.quotepath=false ls-files
}

$violations = @()
foreach ($f in $files) {
    if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { continue }
    # このスクリプト自身は例示のため literal を含む。走査対象外。
    if (($f -replace '\\', '/') -eq $selfPath) { continue }
    $ext = [System.IO.Path]::GetExtension($f).ToLowerInvariant()
    if ($ext -and ($textExtensions -notcontains $ext)) { continue }
    try {
        $lines = Get-Content -LiteralPath $f -ErrorAction Stop
    } catch {
        continue
    }
    if ($null -eq $lines) { continue }
    for ($i = 0; $i -lt $lines.Count; $i++) {
        foreach ($p in $patterns) {
            if ($lines[$i] -match $p) {
                $violations += ('{0}:{1}: {2}' -f $f, ($i + 1), $lines[$i].Trim())
                break
            }
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Output ''
    Write-Output '[no-local-paths] ローカルパスが検出されました。プレースホルダに置換してください:'
    Write-Output '  Windows/Git Bash 形式のユーザーホーム系 (C:\Users\<username>\, C:/Users/<username>/, /c/Users/<username>/ 等) -> %USERPROFILE%\'
    Write-Output '  リポジトリ絶対パス (X:\src\yEdit, X:/src/yEdit, /x/src/yEdit 等)                          -> <repo>'
    Write-Output ''
    $violations | ForEach-Object { Write-Output ('  ' + $_) }
    Write-Output ''
    exit 1
}

Write-Output '[no-local-paths] OK'
