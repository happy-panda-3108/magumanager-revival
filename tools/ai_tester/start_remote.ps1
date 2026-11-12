# Starts one AI tester's PC client WITHOUT a window (engine session, 2026-11-10; user: "ai 테스터 4명 … 각각 다른 구단의 덱").
# Batch mode: no window, no sound, no real mouse; the tester drives it with ma9m_remote.exe <port>. Live server (CDN 127.0.0.1:18080, game 18000).
#   powershell -File start_remote.ps1 -Tester 1..4 [-Restart]
# Tester n uses autotest(n+2)@ma9m.local / (가림) and port 18510+n; its game log is runs\1110\<n>_<name>\game.log.
param([Parameter(Mandatory = $true)][ValidateRange(1, 5)][int]$Tester, [switch]$Restart)
$names = @{ 1 = '1_원작올드팬'; 2 = '2_야구팬첫게임'; 3 = '3_효율파'; 4 = '4_출퇴근라이트'; 5 = '..\1109' }  # 5 = the first tester (autotest2, LG), its notes in runs\1109 (user 11-10: "LG덱도 테스터를 이어감")
$account = if ($Tester -eq 5) { 'autotest2@ma9m.local' } else { "autotest$($Tester + 2)@ma9m.local" }
$port = 18510 + $Tester
$dir = "C:\Ma9MProject\tools\ai_tester\runs\1110\$($names[$Tester])"
New-Item -ItemType Directory -Force $dir | Out-Null
# The build to run: the folder named in tester_build.txt (the coordinator switches it between runs), else builds\tester.
$pointer = 'C:\Ma9MProject\tools\ai_tester\tester_build.txt'
$exe = if (Test-Path $pointer) { (Get-Content $pointer -TotalCount 1).Trim() } else { 'C:\Ma9MProject\port\builds\tester\Ma9M.exe' }
$owner = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty OwningProcess
if ($owner) {
    if (-not $Restart) { Write-Output "already running on port $port (pid $owner); pass -Restart to restart"; exit 0 }
    Stop-Process -Id $owner -Force
    Start-Sleep -Seconds 2
}
# Test clients share one data folder: two started within seconds fought over data.hhj and the loser stayed on
# "Checking..." for good (11-12 00:54, four of five; DataManager.Load fixed in the client from the build after tester_l).
# So wait until the youngest running client is 20 s old.
while ($true) {
    $young = Get-Process Ma9M -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt (Get-Date).AddSeconds(-20) }
    if (-not $young) { break }
    Start-Sleep -Seconds 2
}
$log = Join-Path $dir 'game.log'
if (Test-Path $log) { Move-Item -Force $log (Join-Path $dir ("game_" + (Get-Date -Format 'MMdd_HHmmss') + ".log")) }
$argList = @('-batchmode', '-ma9mremote', "$port", '-ma9mserver', '127.0.0.1:18080', '-ma9mlogin', $account, 'autotest',
    '-screen-width', '1560', '-screen-height', '720', '-logFile', $log)
$p = Start-Process -FilePath $exe -ArgumentList $argList -WindowStyle Hidden -PassThru
Start-Sleep -Milliseconds 500
try { $p.PriorityClass = 'BelowNormal' } catch {}
Write-Output "started tester $Tester ($account) pid $($p.Id) port $port; log $log"
