# 2026-11-05 중계 체력 on league days (user chose (가) "계산 다 끝나고"): waits for the 육성표 plan run to finish, then
# measures the whole bullpen +18 per stat in chains of 10 games and one game at a time. Low priority, data2/build2.
param([int]$Games = 200000)
$S = 'C:\Users\honey\AppData\Local\Temp\claude\c--Ma9MProject\62643804-f5e0-475c-b9b9-dbc0fd01a132\scratchpad\yuk'
Set-Location $S
while (-not (Test-Path (Join-Path $S 'plan.log.exit.txt'))) { Start-Sleep -Seconds 60 }
foreach ($line in Get-Content (Join-Path $S 'plan_env_pen.txt') -Encoding UTF8) {
  if ($line -match '^([A-Z0-9_]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process') }
}
foreach ($mode in 10, 0) {
  [Environment]::SetEnvironmentVariable('TCSIM_SERIES', "$mode", 'Process')
  cmd /c "start """" /b /wait /low /affinity FFF dotnet build2\Ma9MServer.dll data2 --tcsim 150 $Games plan > pen_$mode.log 2>&1"
}
"done $(Get-Date -Format s)" | Out-File (Join-Path $S 'pen.exit.txt') -Encoding ascii
