# 오늘의 미션 보상을 모두 받고 로비로. 받기 전·뒤 MP와 미션 화면의 요약만 돌려준다.
#   powershell -NoProfile -File missions.ps1 -Port 18511
param([Parameter(Mandatory = $true)][int]$Port)
. "$PSScriptRoot\lib.ps1"; Use-Port $Port
if (-not (Go-Lobby)) { "로비로 못 감: $(Scene)"; exit 1 }
$mp0 = Top-MP
Tap-Or '도전과제' 400 386 2500 | Out-Null
if (-not (Wait-For @('보상받기') 15)) { "미션 화면이 안 나옴: $(Scene)"; exit 1 }
$sum = @(Send-R text | Select-Object -Skip 1 | Where-Object { $_ -match '"(미션 완료 횟수|남은 미션 수|오늘 획득 MP)"' -or $_ -match '^(26\d|78\d),1[1-7]\d ' })
for ($k = 1; $k -le 6; $k++) {
    Tap "보상받기#$k" 1000 | Out-Null
    Close-Popups 3 | Out-Null
}
$after = @(Send-R text | Select-Object -Skip 1 | Where-Object { $_ -match '^(26\d|78\d),1[1-7]\d ' } | ForEach-Object { ($_ -replace '^\d+,\d+ ', '') })
Go-Lobby | Out-Null
$mp1 = Top-MP
Say ("미션 화면: 완료·남은·오늘 획득 = " + ($after -join ' / '))
Say ("MP " + ('{0:N0}' -f $mp0) + " → " + ('{0:N0}' -f $mp1) + " (+" + ('{0:N0}' -f ($mp1 - $mp0)) + ") · 장면 " + (Scene))
Done
