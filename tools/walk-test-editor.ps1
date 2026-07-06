# P5 Task 14: Reproduce PC-Talker's per-character line walk on yEdit.Editor.Smoke (--uia).
#   Expand(Char) once, then repeated Move(Char,1) + GetText.
# Correct v2 provider must yield distinct characters per column (Move スパン保持ロジック)。
# Buggy provider yields the same char or empty strings after the first read.
param(
    [string]$Exe = "<repo>\.worktrees\custom-editcontrol-design\tests\yEdit.Editor.Smoke\bin\Debug\net9.0-windows\yEdit.Editor.Smoke.exe"
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# 既知テキストを一時ファイルへ書き出し、smoke に開かせる
$sample = "Hello UIA v2 walk test.`nSecond line for line-move.`n"
$tmp = Join-Path $env:TEMP ("yedit-walk-{0}.txt" -f ([guid]::NewGuid()))
[System.IO.File]::WriteAllText($tmp, $sample, [System.Text.UTF8Encoding]::new($false))

$proc = Start-Process -FilePath $Exe -ArgumentList "--uia", $tmp -PassThru
Start-Sleep -Seconds 2

$AE   = [System.Windows.Automation.AutomationElement]
$TS   = [System.Windows.Automation.TreeScope]
$TP   = [System.Windows.Automation.TextPattern]
$EP   = [System.Windows.Automation.Text.TextPatternRangeEndpoint]
$UNIT = [System.Windows.Automation.Text.TextUnit]

$exitCode = 0
try {
    $root = $AE::RootElement
    $idCond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, "editor")
    $doc = $root.FindFirst($TS::Descendants, $idCond)
    if ($null -eq $doc) { Write-Output "[FAIL] EditorControl not found"; $exitCode = 1; return }
    $tp  = [System.Windows.Automation.TextPattern]$doc.GetCurrentPattern($TP::Pattern)

    $r = $tp.DocumentRange.Clone()
    $r.MoveEndpointByRange($EP::End, $r, $EP::Start)   # collapse to [0,0]
    $r.ExpandToEnclosingUnit($UNIT::Character)          # [0,1]

    $got = @()
    for ($i = 0; $i -lt 5; $i++) {
        $t = $r.GetText(-1)
        $got += $t
        Write-Output ("walk col {0} -> '{1}'" -f $i, $t)
        [void]$r.Move($UNIT::Character, 1)
    }

    # 期待: H, e, l, l, o(距離のある文字列なので distinct が 4 以上)
    $distinct = ($got | Select-Object -Unique).Count
    Write-Output ("distinct value count = {0}" -f $distinct)
    if ($distinct -ge 4) { Write-Output "[PASS] walk reads distinct characters per column" }
    else { Write-Output "[FAIL] walk does NOT advance (Move collapses span) -> only $distinct distinct value(s)"; $exitCode = 1 }
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    if (Test-Path $tmp) { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
}
exit $exitCode
