# Dump all top-level windows for the probe pid + direct AutomationId search from root.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$exe = "<repo>\src\yEdit.UiaProbe\bin\Debug\net9.0-windows\yEdit.UiaProbe.exe"
$proc = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 2

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

try {
    $root = $AE::RootElement
    $tops = $root.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition)
    Write-Output ("Top-level windows total = {0}" -f $tops.Count)
    foreach ($c in $tops) {
        if ($c.Current.ProcessId -eq $proc.Id) {
            $kids = $c.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition)
            Write-Output ("WIN pid-match: Name='{0}' CT={1} Class='{2}' childCount={3}" -f `
                $c.Current.Name, $c.Current.ControlType.ProgrammaticName, $c.Current.ClassName, $kids.Count)
            foreach ($k in $kids) {
                $pats = ($k.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName }) -join ','
                Write-Output ("    child CT={0,-26} Class='{1}' AID='{2}' Name='{3}' Pats=[{4}]" -f `
                    $k.Current.ControlType.ProgrammaticName, $k.Current.ClassName, $k.Current.AutomationId, $k.Current.Name, $pats)
            }
        }
    }

    Write-Output "--- direct search from root for AutomationId=uiaProbeDocument ---"
    $idCond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, "uiaProbeDocument")
    $doc = $root.FindFirst($TS::Descendants, $idCond)
    if ($null -ne $doc) {
        $pats = ($doc.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName }) -join ','
        Write-Output ("FOUND doc: CT={0} Name='{1}' Pats=[{2}]" -f $doc.Current.ControlType.ProgrammaticName, $doc.Current.Name, $pats)
    } else {
        Write-Output "NOT FOUND via root descendant search"
    }
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}
