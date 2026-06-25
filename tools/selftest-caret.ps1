# Self-test (no screen reader): drive Right-arrows and inspect our system-caret log.
# Confirms whether OUR caret pixel advances per character. No UIA client attached,
# so only [sysCaret] EVT lines should appear (provider methods need a UIA client).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

$exe = "<repo>\src\yEdit.UiaProbe\bin\Debug\net9.0-windows\yEdit.UiaProbe.exe"
$log = Join-Path $env:TEMP "yedit-uia-probe.log"

$proc = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 2

$wshell = New-Object -ComObject WScript.Shell
[void]$wshell.AppActivate($proc.Id)
Start-Sleep -Milliseconds 800

# Ensure caret at line head, then move right several times.
[System.Windows.Forms.SendKeys]::SendWait("{HOME}")
Start-Sleep -Milliseconds 200
for ($i = 0; $i -lt 6; $i++) {
    [System.Windows.Forms.SendKeys]::SendWait("{RIGHT}")
    Start-Sleep -Milliseconds 250
}
Start-Sleep -Milliseconds 300

Stop-Process -Id $proc.Id -Force

Write-Output "===== caret-related log lines ====="
Get-Content $log | Select-String -Pattern 'sysCaret|caret|edit' | Select-Object -Last 30
