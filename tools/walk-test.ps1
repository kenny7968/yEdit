# Reproduce PC-Talker's per-character line walk WITHOUT a screen reader:
#   Expand(Char) once, then repeated Move(Char,1) + GetText.
# Correct provider must yield: こ, れ, は, ' ', U, I (chars of line 0).
# Buggy provider yields: こ, '', '', ... (Move collapses the range).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$exe = "<repo>\src\yEdit.UiaProbe\bin\Debug\net9.0-windows\yEdit.UiaProbe.exe"
$proc = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 2

$AE   = [System.Windows.Automation.AutomationElement]
$TS   = [System.Windows.Automation.TreeScope]
$TP   = [System.Windows.Automation.TextPattern]
$EP   = [System.Windows.Automation.Text.TextPatternRangeEndpoint]
$UNIT = [System.Windows.Automation.Text.TextUnit]

try {
    $root = $AE::RootElement
    $idCond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, "uiaProbeDocument")
    $doc = $root.FindFirst($TS::Descendants, $idCond)
    $tp  = [System.Windows.Automation.TextPattern]$doc.GetCurrentPattern($TP::Pattern)

    $r = $tp.DocumentRange.Clone()
    $r.MoveEndpointByRange($EP::End, $r, $EP::Start)   # collapse to [0,0]
    $r.ExpandToEnclosingUnit($UNIT::Character)          # [0,1]

    $got = @()
    for ($i = 0; $i -lt 6; $i++) {
        $t = $r.GetText(-1)
        $got += $t
        Write-Output ("walk col {0} -> '{1}'" -f $i, $t)
        [void]$r.Move($UNIT::Character, 1)
    }

    $expected = @('これは'[0], 'これは'[1], 'これは'[2], ' ', 'U', 'I')
    $joined = ($got -join ',')
    Write-Output ("got      = {0}" -f $joined)
    $distinct = ($got | Select-Object -Unique).Count
    if ($distinct -ge 5) { Write-Output "[PASS] walk reads distinct characters per column" }
    else { Write-Output "[FAIL] walk does NOT advance (Move collapses span) -> only $distinct distinct value(s)" }
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}
