#Requires -Version 5.1
<#
.SYNOPSIS
  main マージ前のローカルゲート。Release ビルド 0 警告+全テスト緑を確認する。
  失敗があれば EXIT 1。典拠: docs/plans/2026-07-13-test-strategy-design.md §2.1
.EXAMPLE
  powershell -File tools\pre-merge-check.ps1
#>
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Invoke-Step {
    param([string]$Name, [scriptblock]$Body)
    Write-Host "==> $Name" -ForegroundColor Cyan
    $global:LASTEXITCODE = 0
    & $Body
    if ($LASTEXITCODE -ne 0) {
        Write-Host "NG: $Name (exit $LASTEXITCODE)" -ForegroundColor Red
        exit 1
    }
}

Invoke-Step 'Release ビルド(警告=エラー)' {
    dotnet build (Join-Path $repoRoot 'yEdit.sln') -c Release -warnaserror
}
Invoke-Step 'Core.Tests' {
    dotnet test (Join-Path $repoRoot 'tests/yEdit.Core.Tests') -c Release --no-build
}
Invoke-Step 'Editor.Tests' {
    dotnet test (Join-Path $repoRoot 'tests/yEdit.Editor.Tests') -c Release --no-build
}
Invoke-Step 'App.Tests' {
    dotnet test (Join-Path $repoRoot 'tests/yEdit.App.Tests') -c Release --no-build
}
Write-Host 'OK: pre-merge チェック全通過' -ForegroundColor Green
exit 0
