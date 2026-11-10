# 2026-11-10: plan mode for the 레전드 slots (legend_plan_env.txt from gen_legend_env.py plan; TCSIM_BATS, TCSIM_PLAN_SLOTS B<n>),
# one process on logical 2-11 (0-1 stay free), idle. -Build picks the scratch build folder (B<n> slots need a build of 11-10 or later).
param([string]$Env = 'legend_plan_env.txt', [string]$Log = 'legend_plan.log', [int]$Games = 200000, [string]$Build = 'build4', [Parameter(Mandatory = $true)][string]$Work)
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
foreach ($line in Get-Content (Join-Path $Here $Env) -Encoding UTF8) {
  if ($line -match '^([A-Z0-9_]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process') }
}
[Environment]::SetEnvironmentVariable('DOTNET_gcServer', '1', 'Process')
[Environment]::SetEnvironmentVariable('DOTNET_GCHeapCount', '6', 'Process')
[Environment]::SetEnvironmentVariable('DOTNET_GCConserveMemory', '5', 'Process')
Set-Location $Work
cmd /c "start """" /b /wait /low /affinity FFC dotnet $Build\Ma9MServer.dll data --tcsim 150 $Games plan > $Log 2>&1"
"plan done $(Get-Date -Format s)" | Out-File (Join-Path $Work "$Log.exit.txt") -Encoding ascii
