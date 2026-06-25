# UIA client verification for yEdit.ScintillaProbe (ASCII only to avoid PS 5.1 encoding issues).
# Proves the whole graft: WM_GETOBJECT interception on the Scintilla window serves our
# UIA provider, and TextPattern reads the Scintilla-backed snapshot. Exercises the new
# byte<->UTF16 conversion via a multibyte selection round-trip.
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

function Write-Check($ok, $msg) {
    if ($ok) { Write-Output "[PASS] $msg" } else { Write-Output "[FAIL] $msg" }
}

try {
    $root = $AE::RootElement
    $idCond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, "uiaProbeDocument")
    $doc = $root.FindFirst($TS::Descendants, $idCond)
    Write-Check ($null -ne $doc) "Found document element on Scintilla window (AutomationId=uiaProbeDocument)"
    if ($null -eq $doc) { return }

    $ctName = $doc.Current.ControlType.ProgrammaticName
    Write-Output ("    ControlType = {0}" -f $ctName)

    $supported = $doc.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName }
    Write-Output ("    Supported patterns = {0}" -f ($supported -join ', '))
    $hasText = $doc.GetCurrentPattern($TP::Pattern)
    Write-Check ($null -ne $hasText) "Exposes TextPattern"
    $tp = [System.Windows.Automation.TextPattern]$hasText

    $text = $tp.DocumentRange.GetText(-1)
    Write-Output ("    DocumentRange length (UTF16) = {0}" -f $text.Length)
    Write-Check ($text.Contains("Scintilla")) "Scintilla document text readable via UIA"

    $sel0 = $tp.GetSelection()
    Write-Check ($sel0.Length -ge 1) "GetSelection returns a range"

    # Multibyte selection round-trip: select first 3 chars (Japanese, 3 bytes each).
    # Exercises UTF16->byte (SetSelection) and byte->UTF16 (RefreshSelection) conversion.
    try {
        $r = $tp.DocumentRange.Clone()
        $r.MoveEndpointByRange($EP::End, $r, $EP::Start)
        [void]$r.MoveEndpointByUnit($EP::End, $UNIT::Character, 3)
        $r.Select()
        Start-Sleep -Milliseconds 300
        $selText = $tp.GetSelection()[0].GetText(-1)
        Write-Output ("    After Select(3 chars), readback length = {0}" -f $selText.Length)
        Write-Check ($selText.Length -eq 3) "Multibyte selection round-trips through byte<->UTF16 (3 chars)"
    } catch {
        Write-Check $false ("Select round-trip threw: {0}" -f $_.Exception.Message)
    }

    # Line enumeration including the empty line (zero-length read unit).
    try {
        $r = $tp.DocumentRange.Clone()
        $r.MoveEndpointByRange($EP::End, $r, $EP::Start)
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
        Write-Check $emptyFound "Empty line exposed as zero-length read unit (no trailing LF)"
    } catch {
        Write-Check $false ("Line enumeration threw: {0}" -f $_.Exception.Message)
    }
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}
