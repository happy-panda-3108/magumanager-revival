# 캡처 대신 화면을 글로 한 번에: 장면, 누를 수 있는 단추, 화면의 글(앞에서 n줄, 또는 낱말이 든 줄만).
#   powershell -NoProfile -File look.ps1 -Port 18511 [-Lines 40] [-Find 낱말]
param([Parameter(Mandatory = $true)][int]$Port, [int]$Lines = 40, [string]$Find = '')
. "$PSScriptRoot\lib.ps1"; Use-Port $Port
"장면 " + (Scene)
Send-R ui | Select-Object -Skip 1
if ($Find) { Send-R text $Find | Select-Object -Skip 1 } else { Send-R text | Select-Object -Skip 1 | Select-Object -First $Lines }
