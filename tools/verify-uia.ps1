# UIA client verification for yEdit.UiaProbe (ASCII only to avoid PS 5.1 encoding issues).
param([string]$Exe = "<repo>\src\yEdit.UiaProbe\bin\Debug\net9.0-windows\yEdit.UiaProbe.exe")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$exe = $Exe
$proc = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 2

$AE   = [System.Windows.Automation.AutomationElement]
$TS   = [System.Windows.Automation.TreeScope]
$TP   = [System.Windows.Automation.TextPattern]
$EP   = [System.Windows.Automation.Text.TextPatternRangeEndpoint]
$UNIT = [System.Windows.Automation.Text.TextUnit]

function Write-Check($ok, $msg) {
    if ($ok) { Write-Output "[PASS] $msg" } else { Write-Output "[FAIL] $msg" }
}

try {
    $root = $AE::RootElement
    $idCond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, "uiaProbeDocument")
    $doc = $root.FindFirst($TS::Descendants, $idCond)
    Write-Check ($null -ne $doc) "Found document element (AutomationId=uiaProbeDocument)"

    $ctName = $doc.Current.ControlType.ProgrammaticName
    Write-Output ("    ControlType = {0}" -f $ctName)

    $supported = $doc.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName }
    Write-Output ("    Supported patterns = {0}" -f ($supported -join ', '))
    $hasText = $doc.GetCurrentPattern($TP::Pattern)
    Write-Check ($null -ne $hasText) "Exposes TextPattern"
    $tp = [System.Windows.Automation.TextPattern]$hasText

    $text = $tp.DocumentRange.GetText(-1)
    Write-Output ("    DocumentRange length = {0}" -f $text.Length)
    Write-Check ($text.Contains("UIA")) "Document text readable via UIA"

    $sel0 = $tp.GetSelection()
    Write-Check ($sel0.Length -ge 1) "GetSelection returns a range"
    Write-Output ("    Initial selection length = {0}" -f $sel0[0].GetText(-1).Length)

    try {
        $r = $tp.DocumentRange.Clone()
        $r.MoveEndpointByRange($EP::End, $r, $EP::Start)
        [void]$r.MoveEndpointByUnit($EP::End, $UNIT::Character, 3)
        $r.Select()
        Start-Sleep -Milliseconds 300
        $selText = $tp.GetSelection()[0].GetText(-1)
        Write-Output ("    After Select, selection length = {0}" -f $selText.Length)
        Write-Check ($selText.Length -eq 3) "ITextRangeProvider.Select round-trips (3 chars)"
    } catch {
        Write-Check $false ("Select round-trip threw: {0}" -f $_.Exception.Message)
    }

    try {
        $r = $tp.DocumentRange.Clone()
        $r.MoveEndpointByRange($EP::End, $r, $EP::Start)  # collapse to document start
        $emptyFound = $false
        for ($i = 0; $i -lt 12; $i++) {
            $lr = $r.Clone()
            $lr.ExpandToEnclosingUnit($UNIT::Line)
            $t = $lr.GetText(-1)
            $shown = ($t -replace "`r", " ") -replace "`n", "<LF>"
            Write-Output ("    line[{0}] len={1} text='{2}'" -f $i, $t.Length, $shown)
            if ($t.Length -eq 0) { $emptyFound = $true }
            $moved = $r.Move($UNIT::Line, 1)
            if ($moved -eq 0) { break }
        }
        Write-Check $emptyFound "Empty line is exposed as a zero-length read unit (no trailing LF)"
    } catch {
        Write-Check $false ("Line enumeration threw: {0}" -f $_.Exception.Message)
    }
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}
