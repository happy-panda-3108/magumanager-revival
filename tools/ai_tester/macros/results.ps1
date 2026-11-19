# 확인하지 않은 결과 받기 → 메인로비 → 로비에 뜬 창 닫기. 결과 줄, 업적 상자, 뜬 창의 첫 글, MP만 돌려준다.
#   powershell -NoProfile -File results.ps1 -Port 18511
param([Parameter(Mandatory = $true)][int]$Port)
. "$PSScriptRoot\lib.ps1"; Use-Port $Port
$s = Scene
if ($s -ne 'Scene_Unconfirmed') { "결과 화면이 아님: $s"; exit 0 }
$mp0 = Top-MP
# 이미 받은 결과면(모두 확인이 없고 메인로비로가 켜짐) 받기를 건너뛴다.
if (-not (Wait-For @('모두 확인', '메인로비로') 25)) { "모두 확인·메인로비로 단추가 안 나옴 (장면 $(Scene))"; exit 1 }
if (Has '모두 확인') {
    Tap '모두 확인' 1300 | Out-Null
    if (Has '확인') { Tap '확인' 2500 | Out-Null }
}
Wait-For @('메인로비로') 40 | Out-Null
# 업적 상자(도전과제완료)는 5초마다 저절로 넘어간다: 다 사라질 때까지 기다린다(상자 글이 결과 줄에 섞이고 누름을 가로챔).
$boxes = @()
$tb = Get-Date
while (((Get-Date) - $tb).TotalSeconds -lt 45) {
    $x = @(Send-R text '도전과제완료' | Select-Object -Skip 1 | Where-Object { $_ -match '^\d' })
    if ($x.Count -eq 0) { break }
    $all = @(Send-R text | Select-Object -Skip 1)
    $i = [Array]::FindIndex([string[]]$all, [Predicate[string]] { param($l) $l -match '도전과제완료' })
    if ($i -ge 0) { $boxes += (($all[$i..([Math]::Min($all.Count - 1, $i + 2))] | ForEach-Object { $_ -replace '^\d+,\d+ "', '' -replace '"$', '' }) -join ' ') }
    Start-Sleep -Milliseconds 2500
}
if ($boxes.Count) { Say ('업적 상자: ' + (($boxes | Select-Object -Unique) -join ' | ')) }
# 결과 줄: 결과 판(Panel_Unconfirmed)의 글만, 같은 높이끼리 한 줄로.
$t = @(Send-R text | Select-Object -Skip 1)
$in = $false
$byY = @{}
foreach ($l in $t) {
    if ($l -match '^\[(.+)\]$') { $in = $Matches[1] -like 'Panel_Unconfirmed*'; continue }
    if ($in -and $l -match '^(\d+),(\d+) "([^"]*)"$') { $y = [int]([int]$Matches[2] / 6); if (-not $byY.ContainsKey($y)) { $byY[$y] = @() }; $byY[$y] += $Matches[3] }
}
$rows = 0
foreach ($k in ($byY.Keys | Sort-Object)) { $row = $byY[$k] -join ' '; if ($row -match '\d+차전') { Say $row; $rows++ } }
Say "결과 $rows 줄(첫 쪽)"
for ($k = 0; $k -lt 3 -and (Scene) -eq 'Scene_Unconfirmed'; $k++) { Tap '메인로비로' 3500 | Out-Null }
Wait-For @() 15 'Scene_MainLobby' | Out-Null
Start-Sleep -Seconds 2
$pop = @(Close-Popups 10)
if ($pop.Count) { Say ("로비 창 $($pop.Count)개: " + ($pop -join ' | ')) }
$mp1 = Top-MP
Say ("MP " + ('{0:N0}' -f $mp0) + " → " + ('{0:N0}' -f $mp1) + " · 장면 " + (Scene))
Done
