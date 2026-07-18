# ローカルパスの混入を検出する健全性チェック。
# 対象は Windows ユーザーホーム下(現ユーザー名込みの C-drive 絶対パス)と、
# 本リポジトリのローカル絶対パスの 2 種。それぞれ以下のプレースホルダに置換して
# 再 commit することで解消できる:
#   Windows ホーム系 -> %USERPROFILE%\
#   リポジトリ系     -> <repo>
#
# 具体の検出リテラルは $userPath / $repoPath を参照。文字列連結で構成して
# いるのは、このファイル自身が自己検出されるのを避けるため。
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

$userPath = 'C:\Users\' + '<username>\'
$repoPath = 'D:\src\' + 'yEdit'
$patterns = @($userPath, $repoPath)

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
            if ($lines[$i].Contains($p)) {
                $violations += ('{0}:{1}: {2}' -f $f, ($i + 1), $lines[$i].Trim())
            }
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Output ''
    Write-Output '[no-local-paths] ローカルパスが検出されました。プレースホルダに置換してください:'
    Write-Output ('  ' + $userPath + '  ->  %USERPROFILE%\')
    Write-Output ('  ' + $repoPath + '    ->  <repo>')
    Write-Output ''
    $violations | ForEach-Object { Write-Output ('  ' + $_) }
    Write-Output ''
    exit 1
}

Write-Output '[no-local-paths] OK'
