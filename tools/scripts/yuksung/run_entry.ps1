# 2026-11-07: staff variants (entry_env.txt) against the 11-02 deck league, one process on logical 2-11 (0-1 stay free), idle.
param([string]$Env = 'entry_env.txt', [string]$Log = 'entry.log', [int]$Games = 4000, [Parameter(Mandatory = $true)][string]$Work)
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
foreach ($line in Get-Content (Join-Path $Here $Env) -Encoding UTF8) {
  if ($line -match '^([A-Z0-9_]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process') }
}
[Environment]::SetEnvironmentVariable('DOTNET_gcServer', '1', 'Process')
[Environment]::SetEnvironmentVariable('DOTNET_GCHeapCount', '6', 'Process')
[Environment]::SetEnvironmentVariable('DOTNET_GCConserveMemory', '5', 'Process')
Set-Location $Work
cmd /c "start """" /b /wait /low /affinity FFC dotnet build3\Ma9MServer.dll data --tcsim 150 $Games entry > $Log 2>&1"
"entry done $(Get-Date -Format s)" | Out-File (Join-Path $Work "$Log.exit.txt") -Encoding ascii
