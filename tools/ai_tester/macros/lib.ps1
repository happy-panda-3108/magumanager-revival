# AI 테스터 매크로 공통 (엔진 세션 2026-11-19, 사용자: "테스터가 실행될 때 사용되는 토큰 양만 줄이면 돼").
# 매크로는 반복 조작을 한 번의 호출로 끝내고 몇 줄만 돌려준다. 단추는 글자로 누르고(ui/tap), 글자가 없으면 예전 좌표로 누른다.
# 쓰는 곳: . "$PSScriptRoot\lib.ps1"; Use-Port <포트>
$script:R = 'C:\Ma9MProject\tools\ai_tester\ma9m_remote.exe'
$script:M9Port = 0
$script:Log = New-Object System.Collections.Generic.List[string]

function Use-Port([int]$p) { $script:M9Port = $p }

function Say([string]$s) { $script:Log.Add($s) }

function Send-R { param([Parameter(ValueFromRemainingArguments = $true)][string[]]$a) & $script:R $script:M9Port @a 2>&1 }

function Scene { $s = (Send-R state | Out-String); if ($s -match 'scene=(\S+)') { $Matches[1] } else { 'none' } }

# Buttons a tap reaches now: objects with X, Y, Label, Name.
function Buttons {
    $out = @()
    foreach ($l in (Send-R ui | Select-Object -Skip 1)) {
        if ($l -match '^(\d+),(\d+) "([^"]*)" (\S+)') { $out += [pscustomobject]@{ X = [int]$Matches[1]; Y = [int]$Matches[2]; Label = $Matches[3]; Name = $Matches[4] } }
    }
    $out
}

function Has([string]$q) { [bool](Buttons | Where-Object { $_.Label -eq $q -or $_.Name -eq $q }) }

# Wait until one of the labels/names is reachable (or the scene is given); true if it came.
function Wait-For([string[]]$q, [int]$sec = 20, [string]$scene = '') {
    $t0 = Get-Date
    while (((Get-Date) - $t0).TotalSeconds -lt $sec) {
        if ($scene -and (Scene) -eq $scene) { return $true }
        if ($q) { $b = Buttons; foreach ($x in $q) { if ($b | Where-Object { $_.Label -eq $x -or $_.Name -eq $x }) { return $true } } }
        Start-Sleep -Milliseconds 700
    }
    $false
}

function Tap([string]$q, [int]$waitMs = 900) { $r = (Send-R tap $q --wait $waitMs | Out-String).Trim(); $r }

# Tap by label, or the old coordinate when the label is not there.
function Tap-Or([string]$q, [int]$x, [int]$y, [int]$waitMs = 900) {
    $r = Tap $q $waitMs
    if ($r -like 'ok*') { return $r }
    Send-R click $x $y --wait $waitMs | Out-Null
    "click $x,$y ($q 없음)"
}

# The top bar's MP (first number after the club name), or $null.
function Top-MP {
    foreach ($l in (Send-R text | Select-Object -Skip 1)) {
        if ($l -match '^(\d+),(\d+) "([\d,]{5,})"$' -and [int]$Matches[2] -lt 30 -and [int]$Matches[1] -gt 600 -and [int]$Matches[1] -lt 720) { return [long]($Matches[3] -replace ',', '') }
    }
    $null
}

# Close popups (a screen with few buttons that offers 건너뛰기/닫기/확인/다음/예), up to n of them; their first text lines are kept.
function Close-Popups([int]$n = 8) {
    $seen = @()
    for ($i = 0; $i -lt $n; $i++) {
        $b = @(Buttons)
        if ($b.Count -eq 0 -or $b.Count -gt 5) { break }
        $pick = $null
        foreach ($want in '건너뛰기', '닫기', '확인', '다음', '예') { $pick = $b | Where-Object { $_.Label -eq $want } | Select-Object -First 1; if ($pick) { break } }
        if (-not $pick) { break }
        $t = @(Send-R text | Select-Object -Skip 1 | Where-Object { $_ -match '^\d' } | Select-Object -First 2 | ForEach-Object { ($_ -replace '^\d+,\d+ ', '') })
        $seen += ($t -join ' / ')
        Send-R click $pick.X $pick.Y --wait 1100 | Out-Null
    }
    $seen
}

# To the lobby from wherever (back key, then the 메인로비 buttons), popups closed.
function Go-Lobby([int]$sec = 30) {
    $t0 = Get-Date
    while (((Get-Date) - $t0).TotalSeconds -lt $sec) {
        $s = Scene
        if ($s -eq 'Scene_MainLobby') { Close-Popups | Out-Null; if ((Scene) -eq 'Scene_MainLobby' -and (Has '도전과제')) { return $true } }
        elseif ($s -eq 'Scene_Unconfirmed') { return $false }
        else
        {
            # 메인로비 단추, 없으면 위 띠의 ← (BtnBack), 그것도 없으면 뒤로 가기.
            $done = $false
            foreach ($q in '메인로비로', '메인로비', 'BtnBack') { if ((Tap $q 1800) -like 'ok*') { $done = $true; break } }
            if (-not $done) { Send-R key esc --wait 1200 | Out-Null }
        }
        Start-Sleep -Milliseconds 600
    }
    (Scene) -eq 'Scene_MainLobby'
}

function Done { $script:Log | ForEach-Object { $_ } }
