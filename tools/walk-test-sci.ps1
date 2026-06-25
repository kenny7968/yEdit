# Reproduce PC-Talker's per-character line walk WITHOUT a screen reader, against
# yEdit.ScintillaProbe: Expand(Char) once, then repeated Move(Char,1) + GetText.
# Correct provider yields distinct chars per column; the old Move-collapse bug yielded
# first-char then empties. The provider is unchanged from the SR-validated build, so this
# must still PASS when backed by Scintilla. (ASCII only for PS 5.1.)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$exe = "<repo>\src\yEdit.ScintillaProbe\bin\Debug\net9.0-windows\yEdit.ScintillaProbe.exe"
$proc = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 3

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

    $distinct = ($got | Select-Object -Unique).Count
    if ($distinct -ge 5) { Write-Output "[PASS] walk reads distinct characters per column ($distinct distinct) - Move span preserved on Scintilla" }
    else { Write-Output "[FAIL] walk does NOT advance (Move collapses span) -> only $distinct distinct value(s)" }
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}
