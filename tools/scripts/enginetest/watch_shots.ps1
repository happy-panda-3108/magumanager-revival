# Engine session 2026-11-19: follow a test client's log and take remote shots when given lines show up.
#   watch_shots.ps1 -Log <client log> -Port <remote port> -Out <dir> -Seconds 240
# Shots: the pitching-change board (and 1.5 s on), every steal scene before its pitch and as the catcher takes it (and 1 s on),
# and the walk's scene after a change.
param([string]$Log, [int]$Port, [string]$Out, [int]$Seconds = 240)
$remote = 'C:\Ma9MProject\tools\ai_tester\ma9m_remote.exe'
New-Item -ItemType Directory -Force $Out | Out-Null
$seen = 0
$n = 0
$t0 = Get-Date
function Shot([string]$name) {
    $script:n++
    $f = Join-Path $Out ("{0:D2}_{1}.png" -f $script:n, $name)
    & $remote $Port shot $f | Out-Null
    "$(Get-Date -Format HH:mm:ss.ff) shot $f"
}
while (((Get-Date) - $t0).TotalSeconds -lt $Seconds) {
    if (Test-Path $Log) {
        $lines = @(Get-Content $Log -ErrorAction SilentlyContinue)
        for ($i = $seen; $i -lt $lines.Count; $i++) {
            $l = $lines[$i]
            if ($l -match 'pitching change: runners on (\S+)') { "$l"; Shot "change_on$($Matches[1])"; Start-Sleep -Milliseconds 1500; Shot "change_later" }
            elseif ($l -match 'play IDLE sit=ST_STEAL') { Start-Sleep -Milliseconds 700; Shot "steal_before_pitch" }
            elseif ($l -match 'play STEALPLAY') { Shot "steal_caught"; Start-Sleep -Milliseconds 1000; Shot "steal_after" }
            elseif ($l -match 'play IDLE sit=ST_FOURBALL') { Start-Sleep -Milliseconds 700; Shot "walk_idle" }
        }
        $seen = $lines.Count
    }
    Start-Sleep -Milliseconds 150
}
"done"
