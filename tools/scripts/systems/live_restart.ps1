# Live server restart (systems session, 2026-11-11 21:00; kept in the repo since 2026-11-13, the scratch copy was lost).
#   -Stage : build the current source into the staging folder only (no downtime).
#   (none) : DB backup → stop → copy the staged build into bin\Release\net8.0 → start run_server.bat without a window → wait.
# Needs the user's OK and a word to the engine session first (CLAUDE.md). Exit code 1 from robocopy is normal.
param([switch]$Stage, [string]$Staging = "$env:TEMP\ma9m_livebuild")
$bin = "C:\Ma9MProject\server\Ma9MServer\bin\Release\net8.0"
if ($Stage) {
    if (Test-Path $Staging) { Remove-Item -Recurse -Force $Staging }
    $out = & dotnet build C:\Ma9MProject\server\Ma9MServer\Ma9MServer.csproj -c Release --no-incremental -o $Staging -nologo -v q 2>&1 | Out-String
    ($out -split "`n" | Where-Object { $_ -match ": error |Error\(s\)|Warning\(s\)" }) -join "`n"
    "staged: $((Get-Item "$Staging\Ma9MServer.dll").LastWriteTime)"
    exit
}
if (-not (Test-Path "$Staging\Ma9MServer.dll")) { "no staged build"; exit 1 }
$stamp = Get-Date -Format "yyyyMMdd_HHmm"
& python -I -c "import sqlite3,sys; s=sqlite3.connect('file:'+sys.argv[1]+'?mode=ro',uri=True); d=sqlite3.connect(sys.argv[2]); s.backup(d); d.close(); s.close()" "C:\Ma9MProject\server\data\ma9m.db" "C:\Ma9MProject\server\data\backup\ma9m_before_restart_$stamp.db"
"backup ma9m_before_restart_$stamp.db"
$owner = Get-NetTCPConnection -State Listen -LocalPort 18000 -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty OwningProcess
$t0 = Get-Date
# 2026-11-12: kill the parent cmd first. cmd re-reads run_server.bat after the exe exits, so an edited bat ran the old exe again (00:38).
if ($owner) { $parent = (Get-CimInstance Win32_Process -Filter "ProcessId=$owner").ParentProcessId; if ($parent) { Stop-Process -Id $parent -Force -ErrorAction SilentlyContinue }; Stop-Process -Id $owner -Force; "stopped $owner (cmd $parent) at $($t0.ToString('HH:mm:ss'))" }
for ($i = 0; $i -lt 20 -and (Get-Process -Name Ma9MServer -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$bin*" }); $i++) { Start-Sleep -Milliseconds 500 }
robocopy $Staging $bin /E /R:3 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
$r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "cmd /c C:\Ma9MProject\server\run_server.bat"; CurrentDirectory = "C:\Ma9MProject\server" }
"started (rc $($r.ReturnValue))"
for ($i = 0; $i -lt 120; $i++) { Start-Sleep -Milliseconds 500; if (Get-NetTCPConnection -State Listen -LocalPort 18000 -ErrorAction SilentlyContinue) { break } }
"listening at $((Get-Date).ToString('HH:mm:ss')), down $([int]((Get-Date) - $t0).TotalSeconds) s"
