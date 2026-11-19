# 시범경기 N판(SKIP으로 바로 결과). 판마다 결과 창의 첫 글 한 줄, 끝에 MP만 돌려준다.
#   powershell -NoProfile -File exhibition.ps1 -Port 18511 [-Games 10] [-Grade HARD|NORMAL|EASY]
# 흐름(테스터 NC의 exh_hard.ps1을 글자 누르기로): 로비 → 대회 → 시범경기 → 난이도 → 도전하기 → 확인 → PUSH → 경기시작 →
# 중계 → SKIP → 예 → 결과 창 닫기 → 메인로비. 글자가 없는 단추는 예전 좌표로 누른다.
param([Parameter(Mandatory = $true)][int]$Port, [int]$Games = 10, [string]$Grade = 'HARD')
. "$PSScriptRoot\lib.ps1"; Use-Port $Port
$gx = @{ EASY = 155; NORMAL = 240; HARD = 325 }[$Grade.ToUpper()]
if (-not $gx) { $gx = 325 }
$mp0 = $null
for ($g = 1; $g -le $Games; $g++) {
    if (-not (Go-Lobby)) { Say "${g}판: 로비로 못 감 ($(Scene)) — 결과 화면이면 results.ps1 먼저"; break }
    if ($null -eq $mp0) { $mp0 = Top-MP }
    Tap-Or '대회' 480 386 1500 | Out-Null
    Tap-Or '시범경기' 300 175 1500 | Out-Null
    if (-not (Wait-For @() 12 'Scene_ExhibitionGame')) { Say "${g}판: 시범경기 화면이 안 나옴 ($(Scene))"; break }
    Start-Sleep -Seconds 2
    $left = @(Send-R text | Select-Object -Skip 1 | Where-Object { $_ -match '남은|횟수' } | Select-Object -First 1)
    Tap-Or $Grade.ToUpper() $gx 105 1500 | Out-Null
    Tap-Or '도전하기' 240 250 2000 | Out-Null
    if (Has '확인') { Tap '확인' 2000 | Out-Null } else { Send-R click 410 294 --wait 2000 | Out-Null }
    $b = @(Buttons)
    if (($b | Where-Object { $_.Label -match '횟수|부족' }) -or ((Send-R text | Out-String) -match '모두 사용|횟수가 부족')) { Say "${g}판: 남은 시범경기 없음"; Close-Popups 2 | Out-Null; break }
    Tap-Or 'PUSH' 550 382 5000 | Out-Null
    Tap-Or '경기시작' 707 410 1500 | Out-Null
    if (-not (Wait-For @() 25 'Scene_BroadCast')) { Say "${g}판: 중계가 안 열림 ($(Scene))"; break }
    # SKIP(중계 위 오른쪽, 이름 Btn_Chat) → 예 → 결과 창(확인) → 스코어보드(닫기)
    Wait-For @('Btn_Chat') 20 | Out-Null
    Start-Sleep -Seconds 2
    if (-not ((Tap 'Btn_Chat' 1200) -like 'ok*')) { Send-R click 818 13 --wait 1200 | Out-Null }
    if (Wait-For @('예') 8) { Tap '예' 1500 | Out-Null }
    Wait-For @('확인') 30 | Out-Null
    $res = @(Send-R text | Select-Object -Skip 1 | Where-Object { $_ -match '보상으로|승 :|패 :' } | ForEach-Object { ($_ -replace '^\d+,\d+ "', '' -replace '"$', '' -replace '경기가 종료되었습니다. ', '' -replace ' 확인을 누르면.*', '') })
    Say ("${g}판: " + ($res -join ' · '))
    Tap '확인' 2000 | Out-Null
    if (Wait-For @('닫기') 10) { Tap '닫기' 2500 | Out-Null }
    Wait-For @() 15 'Scene_MainLobby' | Out-Null
}
Go-Lobby | Out-Null
$mp1 = Top-MP
Say ("MP " + ('{0:N0}' -f $mp0) + " → " + ('{0:N0}' -f $mp1) + " · 장면 " + (Scene))
Done
