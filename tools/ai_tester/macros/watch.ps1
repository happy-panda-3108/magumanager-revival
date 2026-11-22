# 생중계 따라가기: 플레이 줄(타석 결과·주자·도루·번트·투수 교체)을 생기는 대로 한 줄씩 돌려준다. 중계가 끝나면 멈춘다.
#   powershell -NoProfile -File watch.ps1 -Port 18511 [-Seconds 180] [-ShotOn '번트|도루|교체|실책'] [-ShotDir <폴더>] [-ShotNo 1503] [-MaxShots 2] [-Pitches]
# -ShotOn 글이 나오면 그 순간의 3D 장면을 작은 캡처(480)로 -ShotDir\<번호>.png에 남긴다. 한 번 부를 때 3분 안팎이 알맞다(도구 호출 한 번).
# 테스터 1 NC(15회차, 11-20)의 t\poll.ps1을 매크로로 옮김.
# (11-20 오후 고침, tester_1120c부터) 3D 중계 화면에는 도루·번트·투수 교체 글이 없다(06:30 회차 두 테스터가 네 번 보며 한 번도 못 봄).
#   그 글은 LIVE-SNS의 기록에만 있어, 원격 명령 sns로 그 기록을 읽는다. 화면은 3D 중계에 그대로 두므로 캡처는 장면을 찍는다.
#   투구 한 개씩의 줄("- 3구 볼")은 -Pitches를 줄 때만 낸다. sns가 없는 옛 빌드에서는 예전처럼 화면 글이 바뀔 때마다 낸다.
# 줄은 읽히는 대로 바로 나온다(앞에서 돌릴 것: 도구 시간 제한을 -Seconds보다 30초 넉넉히).
param([Parameter(Mandatory = $true)][int]$Port, [int]$Seconds = 180, [int]$EveryMs = 1200, [string]$ShotOn = '', [string]$ShotDir = '', [int]$ShotNo = 0, [int]$MaxShots = 2, [switch]$Pitches)
. "$PSScriptRoot\lib.ps1"; Use-Port $Port
function Say([string]$s) { [Console]::Out.WriteLine($s); [Console]::Out.Flush() }
# 투수 교체는 SNS에서 "투수 교체 A → B", 3D 교체 창에서는 "A CHANGE B"로 읽힌다.
$ShotRe = if ($ShotOn -match '교체') { "$ShotOn|CHANGE" } else { $ShotOn }
function Tag([string]$t) {
    if ($t -match '투수 교체|CHANGE') { return '[투수 교체] ' }
    if ($t -match '도루') { return '[도루] ' }
    if ($t -match '번트') { return '[번트] ' }
    if ($t -match '실책') { return '[실책] ' }
    return ''
}
function Shoot([string]$t) {
    if ($ShotRe -and $ShotDir -and $script:shots -lt $MaxShots -and $t -match $ShotRe) {
        $p = Join-Path $ShotDir ('{0}.png' -f ($ShotNo + $script:shots))
        Send-R shot $p 480 | Out-Null
        $script:shots++
        Say ('  캡처 ' + $p)
    }
}
# 장면을 미리 알고 찍기(tester_1120d부터 sns 머리줄 now=장면:종류:투구수): 기록 줄은 플레이가 끝난 뒤에 생겨 그때 찍으면 다음 타자가 찍힌다.
# 고른 종류의 장면이 시작되면 투구 한 개 시간(앞 장면들로 잼)으로 마지막 투구 즈음과 1.5초 뒤 두 번 찍는다.
$sitWords = @{ '도루' = 'STEAL'; '번트' = 'BUNT'; '병살' = 'DOUBLEPLAY'; '실책' = 'ERROR'; '홈런' = 'HR'; '2루타' = '2B'; '3루타' = '3B' }
$sitWant = @(foreach ($w in $sitWords.Keys) { if ($ShotRe -and $w -match $ShotRe) { $sitWords[$w] } })
$sitName = @{ STEAL = '도루'; BUNT = '번트'; DOUBLEPLAY = '병살'; ERROR = '실책'; HR = '홈런'; '2B' = '2루타'; '3B' = '3루타' }
$spp = 2.0          # 투구 한 개에 걸리는 초(장면이 끝날 때마다 고침)
$curScene = -1; $sceneAt = Get-Date; $sceneNp = 1; $plan = @(); $armed = $false
$end = (Get-Date).AddSeconds($Seconds)
$script:shots = 0
$lines = 0
$next = -1
$probe = @(Send-R sns)
$useSns = "$($probe | Select-Object -First 1)" -match '^ok sns'
if (-not $useSns) { Say '(이 빌드에는 sns가 없어 화면 글로 따라갑니다. 도루·번트는 화면 글에 안 나옵니다)' }
$seen = @{}
while ((Get-Date) -lt $end) {
    if ($useSns) {
        $out = if ($next -lt 0) { $probe } else { @(Send-R sns $next) }
        $head = "$($out | Select-Object -First 1)"
        if ($head -notmatch 'scene=Scene_(BroadCast|SNSBroadcast)') { Say ((Get-Date -Format 'HH:mm:ss') + ' 중계 끝: ' + $head); break }
        if ($head -match 'total=(\d+)') { $total = [int]$Matches[1] } else { $total = 0 }
        if ($next -ge 0 -and $total -lt $next) { $next = 0; continue }  # 새 경기(기록이 처음부터)
        $hasNow = $head -match 'now=(\d+):(\w+):(\d+)'
        if ($hasNow) {
            $sc = [int]$Matches[1]; $sit = $Matches[2]; $np = [math]::Max(1, [int]$Matches[3])
            # play=(tester_1120f부터) 중계 진행 상태: 공을 친 순간 HIT·INPLAY, 도루 STEALPLAY. 그때 바로 찍는다.
            $play = if ($head -match 'play=(\w+)') { $Matches[1] } else { '' }
            if ($sc -ne $curScene) {
                if ($curScene -ge 0) {
                    $dur = ((Get-Date) - $sceneAt).TotalSeconds
                    if ($dur -gt 2 -and $dur -lt 60) { $spp = [math]::Min(4.0, [math]::Max(1.0, 0.7 * $spp + 0.3 * (($dur - 3.0) / $sceneNp))) }
                }
                $curScene = $sc; $sceneAt = Get-Date; $sceneNp = $np; $plan = @(); $armed = $false
                if ($sitWant -contains $sit -and $ShotDir -and $script:shots -lt $MaxShots) {
                    if ($play) {
                        $armed = $true
                        Say ((Get-Date -Format 'HH:mm:ss') + " 다음 장면: $($sitName[$sit]) (투구 $np, 플레이가 시작되면 캡처)")
                    } else {
                        $t0 = ($np - 1) * $spp + 0.6
                        $plan = @($sceneAt.AddSeconds($t0), $sceneAt.AddSeconds($t0 + 1.5))
                        Say ((Get-Date -Format 'HH:mm:ss') + " 다음 장면: $($sitName[$sit]) (투구 $np, 약 $([math]::Round($t0))초 뒤 캡처)")
                    }
                }
            }
            if ($armed -and $play -match '^(HIT|INPLAY|STEALPLAY|HOMERUNINPLAY)$') {
                $armed = $false
                $plan = @((Get-Date), (Get-Date).AddSeconds(1.2))
            }
            while ($plan.Count -gt 0 -and (Get-Date) -ge $plan[0] -and $script:shots -lt $MaxShots) {
                $p = Join-Path $ShotDir ('{0}.png' -f ($ShotNo + $script:shots))
                Send-R shot $p 480 | Out-Null
                $script:shots++
                Say ('  캡처 ' + $p)
                $plan = @($plan | Select-Object -Skip 1)
            }
        }
        foreach ($l in ($out | Select-Object -Skip 1)) {
            if ($l -notmatch '^(\d+) (.*)$') { continue }
            $t = $Matches[2]
            if (-not $Pitches -and $t -match '^- \d+구') { continue }
            Say ((Get-Date -Format 'HH:mm:ss') + ' ' + (Tag $t) + $t)
            $lines++
            if (-not $hasNow) { Shoot $t }
        }
        $next = $total
        if ($plan.Count -gt 0 -or $armed) { Start-Sleep -Milliseconds 300; continue }
    } else {
        $out = @(Send-R text)
        $st = "$($out | Select-Object -First 1)"
        if ($st -notmatch 'Scene_(BroadCast|SNSBroadcast)') { Say ((Get-Date -Format 'HH:mm:ss') + ' 중계 끝: ' + $st); break }
        $line = @(foreach ($l in $out) { if ($l -match '^\d+,\d+ "(.*)"$') { $Matches[1] } })
        $tail = if ($line.Count -gt 5) { $line[1..($line.Count - 1)] } else { $line }
        $key = ($tail -join '|')
        if ($key -match 'KM$' -or $seen.ContainsKey($key)) { Start-Sleep -Milliseconds $EveryMs; continue }
        $seen[$key] = 1
        Say ((Get-Date -Format 'HH:mm:ss') + ' ' + (Tag $key) + $key)
        $lines++
        Shoot $key
    }
    Start-Sleep -Milliseconds $EveryMs
}
Say "새 줄 $lines · 캡처 $($script:shots) · 장면 $(Scene)"
