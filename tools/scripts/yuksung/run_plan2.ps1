# 2026-11-05 evening: the 육성표 again on the new pitcher cards and rules (live since 19:35), then the bullpen +18 per stat
# on league days and one game at a time (user chose (가) for 중계 체력; the new rules tie the rest rules to 체력).
param([int]$Games = 200000)
$S = 'C:\Users\honey\AppData\Local\Temp\claude\c--Ma9MProject\62643804-f5e0-475c-b9b9-dbc0fd01a132\scratchpad\yuk'
Set-Location $S
function Load-Env($file) {
  foreach ($line in Get-Content (Join-Path $S $file) -Encoding UTF8) {
    if ($line -match '^([A-Z0-9_]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process') }
  }
}
Load-Env 'plan_env.txt'
cmd /c "start """" /b /wait /low /affinity FFF dotnet build3\Ma9MServer.dll data3 --tcsim 150 $Games plan > plan2.log 2>&1"
"plan done $(Get-Date -Format s)" | Out-File (Join-Path $S 'plan2.exit.txt') -Encoding ascii
Load-Env 'plan_env_pen.txt'
foreach ($mode in 10, 0) {
  [Environment]::SetEnvironmentVariable('TCSIM_SERIES', "$mode", 'Process')
  cmd /c "start """" /b /wait /low /affinity FFF dotnet build3\Ma9MServer.dll data3 --tcsim 150 $Games plan > pen2_$mode.log 2>&1"
}
"pen done $(Get-Date -Format s)" | Out-File (Join-Path $S 'pen2.exit.txt') -Encoding ascii
