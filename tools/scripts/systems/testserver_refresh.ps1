# Test server refresh (systems session, 2026-11-19; the scratch copy of 11-11 was lost). Ports 18100/18180, live is untouched.
#   1. stops a running test server (the one listening on 18100 only)
#   2. builds the current server source into testserver\bin
#   3. copies the live data: ma9m.db by sqlite's online backup (read-only on the live file), tables\ sim\ fences\ uniforms.tsv
#      (testserver\data\cdn stays the link to the live CDN)
#   4. starts testserver\run_testserver.bat without a window and waits for 18100
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools\scripts\systems\testserver_refresh.ps1 [-NoData]
param([switch]$NoData)
$ts = "C:\Ma9MProject\testserver"
$live = "C:\Ma9MProject\server\data"
$owner = Get-NetTCPConnection -State Listen -LocalPort 18100 -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty OwningProcess
if ($owner) {
    $parent = (Get-CimInstance Win32_Process -Filter "ProcessId=$owner").ParentProcessId
    if ($parent) { Stop-Process -Id $parent -Force -ErrorAction SilentlyContinue }
    Stop-Process -Id $owner -Force
    "stopped test server $owner"
    Start-Sleep -Seconds 1
}
$out = & dotnet build C:\Ma9MProject\server\Ma9MServer\Ma9MServer.csproj -c Release -o "$ts\bin" -nologo -v q 2>&1 | Out-String
($out -split "`n" | Where-Object { $_ -match ": error |Error\(s\)" }) -join "`n"
if (-not $NoData) {
    & python -I -c "import sqlite3,sys; s=sqlite3.connect('file:'+sys.argv[1]+'?mode=ro',uri=True); d=sqlite3.connect(sys.argv[2]); s.backup(d); d.close(); s.close()" "$live\ma9m.db" "$ts\data\ma9m.db"
    foreach ($d in "tables", "sim", "fences") { robocopy "$live\$d" "$ts\data\$d" /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null }
    Copy-Item "$live\uniforms.tsv" "$ts\data\uniforms.tsv" -Force
    "data copied $(Get-Date -Format 'HH:mm:ss')"
}
$r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "cmd /c $ts\run_testserver.bat"; CurrentDirectory = $ts }
"started (rc $($r.ReturnValue))"
for ($i = 0; $i -lt 120; $i++) { Start-Sleep -Milliseconds 500; if (Get-NetTCPConnection -State Listen -LocalPort 18100 -ErrorAction SilentlyContinue) { break } }
"listening 18100: $([bool](Get-NetTCPConnection -State Listen -LocalPort 18100 -ErrorAction SilentlyContinue)) at $(Get-Date -Format 'HH:mm:ss')"
