# Starts the PC client for the AI screen tester (engine session, 2026-11-09). Windowed at the phone's shape (19.5:9,
# 1560x720) and logged in once with a test account (-ma9mlogin keeps the user's own saved login). Only test accounts:
# autotest@ma9m.local (has a club) or autotest2@ma9m.local (club creation), password autotest (CLAUDE.md).
#   powershell -File start_game.ps1 [-Account autotest2@ma9m.local] [-Restart]
param([string]$Account = 'autotest2@ma9m.local', [switch]$Restart)
if ($Account -notmatch '^autotest2?@ma9m\.local$') { throw 'test accounts only' }
$exe = 'C:\Ma9MProject\port\builds\win\Ma9M.exe'
$running = Get-Process Ma9M -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
if ($running) {
    if (-not $Restart) { Write-Output "already running (pid $($running.Id)); pass -Restart to restart"; exit 0 }
    $running | Stop-Process -Force
    Start-Sleep -Seconds 2
}
$p = Start-Process -FilePath $exe -ArgumentList @('-ma9mlogin', $Account, 'autotest', '-screen-fullscreen', '0', '-screen-width', '1560', '-screen-height', '720') -PassThru
Write-Output "started pid $($p.Id)"
