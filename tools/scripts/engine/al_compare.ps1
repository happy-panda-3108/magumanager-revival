param(
    [Parameter(Mandatory = $true)][string]$Bin,
    [Parameter(Mandatory = $true)][string]$Data,
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Teams = '잠실허슬두,마산공룡단,출근길위즈,AI테스터,기댓값레이더스',
    [string]$Modes = 'free,keep',
    [string]$EnvA = '',
    [string]$EnvB = ''
)
# 자동 편성 comparison on a data copy (engine session; in the repo since 2026-11-14). For each team and mode it runs
# `Ma9MServer.exe <Data> --autolineup <team> [keep]` once with the environment EnvA ("NAME=value;NAME=value") and once
# with EnvB, and writes the summary lines ([autolineup] now / advice / games, and the pitcher lines) to $Out (UTF-8).
# One process at a time, BelowNormal (feedback-cpu-headroom). Save this file as UTF-8 with BOM (Korean team names).
$ErrorActionPreference = 'Stop'
$lines = New-Object System.Collections.Generic.List[string]
function Run-One([string]$team, [string]$mode, [string]$envSpec, [string]$tag) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = Join-Path $Bin 'Ma9MServer.exe'
    $a = "`"$Data`" --autolineup $team"
    if ($mode -eq 'keep') { $a += ' keep' }
    $psi.Arguments = $a
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.StandardOutputEncoding = [Text.Encoding]::GetEncoding(949)
    $psi.EnvironmentVariables['MA9M_TRAIT_V2'] = '1'
    $psi.EnvironmentVariables['MA9M_GAME_PORT'] = '18990'
    foreach ($kv in ($envSpec -split ';' | Where-Object { $_ -match '=' })) {
        $k, $v = $kv -split '=', 2
        $psi.EnvironmentVariables[$k.Trim()] = $v.Trim()
    }
    $p = [Diagnostics.Process]::Start($psi)
    try { $p.PriorityClass = 'BelowNormal' } catch {}
    $o = $p.StandardOutput.ReadToEnd()
    $p.WaitForExit()
    foreach ($l in $o -split "`n") {
        if ($l -match '\[autolineup\]\s+(games \(.*|advice\s+\d+/\d+.*|now\s+\d+/\d+.*|\s*advice\s+투수.*|\s*now\s+투수.*)') {
            $lines.Add("$team $mode $tag | " + ($Matches[1].Trim() -replace '\| graph mean.*$', ''))
        }
    }
}
foreach ($team in $Teams.Split(',')) {
    foreach ($mode in $Modes.Split(',')) {
        Run-One $team $mode $EnvA 'A'
        Run-One $team $mode $EnvB 'B'
    }
}
[IO.File]::WriteAllLines($Out, $lines, [Text.Encoding]::UTF8)
Write-Output "wrote $($lines.Count) lines to $Out"
