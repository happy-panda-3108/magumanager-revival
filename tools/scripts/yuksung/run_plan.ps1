# 2026-11-05 육성표 재계산 (user: 코칭스태프 보너스 + 트레이닝북 +6). Plan run on a data copy, low priority, 10 cores.
# Usage: run_plan.ps1 <games> <log>
param([int]$Games = 200000, [string]$Log = 'plan.log')
$S = 'C:\Users\honey\AppData\Local\Temp\claude\c--Ma9MProject\62643804-f5e0-475c-b9b9-dbc0fd01a132\scratchpad\yuk'
Set-Location $S
foreach ($line in Get-Content (Join-Path $S 'plan_env.txt') -Encoding UTF8) {
  if ($line -match '^([A-Z0-9_]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process') }
}
cmd /c "start """" /b /wait /low /affinity 3FF dotnet build\Ma9MServer.dll data --tcsim 150 $Games plan > $Log 2>&1"
"exit $LASTEXITCODE $(Get-Date -Format s)" | Out-File (Join-Path $S "$Log.exit.txt") -Encoding ascii
