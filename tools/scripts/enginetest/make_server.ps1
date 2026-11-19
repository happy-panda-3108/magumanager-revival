# Engine session (2026-11-19, kept so it is not rewritten each time: user "똑같은 작업을 할 때마다 … 다시 … 그럴 필요는 없잖아?"):
# a scratch server built from the current sources on a copy of the live data, ports 18200/18280, for 3D checks with a tester
# build (bunt scripts, pitching-change runners, steal counts). Work folder: %TEMP%\ma9m_enginetest (not in the repo).
#   powershell -File make_server.ps1            build + fresh data copy (~15 s)
#   run_server.bat [WANT]                       start it (WANT = MA9M_EXHIBITION_WANT: bunt | steal | changewalk | hr4 | walkoff …)
#   leaguetest.bat                              run the league clock 4 h ahead on the copy (no ports)
# Then a client: tester build -batchmode -ma9mremote <port> -ma9mserver 127.0.0.1:18280 -ma9mautologin autotest@ma9m.local autotest
#   -ma9mexhibition video [-ma9mscriptshots <dir> -ma9mshotstep 0.25] [-ma9mtrace]; watch_shots.ps1 takes remote shots on log lines.
# A killed test leaves exhibition_match for user_code 1 in the copy: delete it with the server stopped before the next run.
# The data copy has a junction pdata\cdn → server\data\cdn: remove it (cmd /c rmdir) before deleting the work folder.
$ErrorActionPreference = 'Stop'
$w = Join-Path $env:TEMP 'ma9m_enginetest'
New-Item -ItemType Directory -Force $w | Out-Null
$log = Join-Path $w 'make_server.log'
"start $(Get-Date -Format HH:mm:ss)" | Out-File $log -Encoding utf8
robocopy C:\Ma9MProject\sim\Ma9MSim "$w\sim\Ma9MSim" /E /XD bin obj /NFL /NDL /NJH /NJS | Out-Null
robocopy C:\Ma9MProject\server\Ma9MServer "$w\server\Ma9MServer" /E /XD bin obj /NFL /NDL /NJH /NJS | Out-Null
if (-not (Test-Path "$w\port")) { cmd /c mklink /J "$w\port" C:\Ma9MProject\port | Out-Null }
$b = dotnet build "$w\server\Ma9MServer\Ma9MServer.csproj" -c Release -o "$w\srvbin" -v q -nologo -nodeReuse:false 2>&1
$b | Select-String -Pattern ' error |Build succeeded' | ForEach-Object { $_.Line } | Out-File $log -Append -Encoding utf8
$pd = "$w\pdata"
New-Item -ItemType Directory -Force $pd | Out-Null
foreach ($d in 'fences', 'sim', 'tables') { robocopy "C:\Ma9MProject\server\data\$d" "$pd\$d" /E /NFL /NDL /NJH /NJS | Out-Null }
Copy-Item C:\Ma9MProject\server\data\uniforms.tsv $pd -Force
if (-not (Test-Path "$pd\cdn")) { cmd /c mklink /J "$pd\cdn" C:\Ma9MProject\server\data\cdn | Out-Null }
& python -I -c "import sqlite3,sys; s=sqlite3.connect('file:C:/Ma9MProject/server/data/ma9m.db?mode=ro',uri=True); d=sqlite3.connect(sys.argv[1]); s.backup(d); d.close(); s.close(); print('db copied')" "$pd\ma9m.db" 2>&1 | Out-File $log -Append -Encoding utf8
Copy-Item "$PSScriptRoot\run_server.bat", "$PSScriptRoot\leaguetest.bat" $w -Force
"done $(Get-Date -Format HH:mm:ss) in $w" | Out-File $log -Append -Encoding utf8
Get-Content $log
