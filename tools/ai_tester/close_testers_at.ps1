# Closes the AI tester clients (ports 18511-18516 only; the systems session's 18519 and anything else stay) at a given time.
# Engine session, 2026-11-19: the night runs keep the clients up until the screensaver may be lifted (CLAUDE.md rule 9, 08:00).
#   powershell -File close_testers_at.ps1 -At "08:00:30"
param([string]$At = "08:00:30")
$target = [datetime]::Today.Add([timespan]::Parse($At))
if ($target -lt (Get-Date)) { $target = $target.AddDays(1) }
while ((Get-Date) -lt $target) { Start-Sleep -Seconds 20 }
$log = "C:\Ma9MProject\tools\ai_tester\close_testers.log"
foreach ($port in 18511..18516) {
    $owner = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty OwningProcess
    if ($owner) {
        $p = Get-Process -Id $owner -ErrorAction SilentlyContinue
        if ($p -and $p.ProcessName -eq 'Ma9M') {
            Stop-Process -Id $owner -Force
            "$(Get-Date -Format 'MM-dd HH:mm:ss') closed port $port pid $owner" | Out-File -Append -Encoding utf8 $log
        }
    }
}
