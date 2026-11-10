# 2026-11-10 (user: "한셀 육성표에서 내가 조금씩 수정해두었는데, 거기에 이어서 작업해주면 될 것 같음"): edits a user's 한셀 book in place
# of rebuilding it, so the user's numbers, colours and formulas stay. A JSON spec (book_spec.py writes it) lists the cells:
#   { "src": <.cell/.xlsx>, "out_dir": <dir>, "base": <output name without extension>, "sheet": "육성 계산기",
#     "values": [[addr, value], ...]   (null clears; a string starting with "=" is written as a formula)
#     "fills":  [[range, "FFRRGGBB"], ...], "fonts": [[range, "FFRRGGBB"], ...],
#     "comments": [[addr, text], ...]  (text "" removes the comment) }
# Opens the source in a hidden 한셀 2024 (HCell.Application), writes, recalculates, saves <base>.cell and <base>.xlsx, then
# reopens both and prints the written cells back. Never run it on a file the user has open in 한셀.
param([Parameter(Mandatory = $true)][string]$Spec)
$s = Get-Content -Raw -Encoding UTF8 $Spec | ConvertFrom-Json
New-Item -ItemType Directory -Force $s.out_dir | Out-Null
function Ole([string]$argb) {
  $r = [Convert]::ToInt32($argb.Substring(2, 2), 16); $g = [Convert]::ToInt32($argb.Substring(4, 2), 16); $b = [Convert]::ToInt32($argb.Substring(6, 2), 16)
  return $r + 256 * $g + 65536 * $b
}
$h = New-Object -ComObject HCell.Application
try {
  $h.DisplayAlerts = $false
  $wb = $h.Workbooks.Open((Resolve-Path $s.src).Path)
  $ws = $wb.Worksheets.Item($s.sheet)
  foreach ($v in $s.values) {
    $cell = $ws.Range($v[0])
    if ($null -eq $v[1]) { $cell.ClearContents() | Out-Null }
    elseif ($v[1] -is [string] -and $v[1].StartsWith('=')) { $cell.Formula = $v[1] }
    elseif ($v[1] -is [string]) { $cell.Value2 = [string]$v[1] }
    else { $cell.Formula = [string]$v[1] }   # a number, entered as typed (HCell's Value2 takes strings only)
  }
  # "extra": [[sheet, addr, value], ...] writes cells of other sheets the same way (the 스탯 가치 rows of new players).
  foreach ($e in $s.extra) {
    $cell = $wb.Worksheets.Item($e[0]).Range($e[1])
    if ($null -eq $e[2]) { $cell.ClearContents() | Out-Null }
    elseif ($e[2] -is [string]) { $cell.Value2 = [string]$e[2] }
    else { $cell.Formula = [string]$e[2] }
  }
  foreach ($f in $s.fills) { $ws.Range($f[0]).Interior.Color = (Ole $f[1]) }
  foreach ($f in $s.fonts) { $ws.Range($f[0]).Font.Color = (Ole $f[1]) }
  foreach ($c in $s.comments) {
    $cell = $ws.Range($c[0])
    if ($null -ne $cell.Comment) { $cell.Comment.Delete() | Out-Null }
    if ($c[1] -ne '') { $cell.AddComment([string]$c[1]) | Out-Null }
  }
  $h.CalculateFull()
  $wb.Saved = $false
  $cellPath = Join-Path (Resolve-Path $s.out_dir).Path ($s.base + '.cell')
  $xlsxPath = Join-Path (Resolve-Path $s.out_dir).Path ($s.base + '.xlsx')
  $wb.SaveAs($cellPath)
  $wb.Close($false)
  $wb = $h.Workbooks.Open($cellPath)
  $h.CalculateFull()
  $wb.Saved = $false
  $wb.SaveAs($xlsxPath, 51)
  $wb.Close($false)
  foreach ($p in @($cellPath, $xlsxPath)) {
    $wb = $h.Workbooks.Open($p)
    $ws = $wb.Worksheets.Item($s.sheet)
    $bad = @()
    foreach ($v in $s.values) {
      $got = $ws.Range($v[0]).Value2
      $want = $v[1]
      if ($want -is [string] -and $want.StartsWith('=')) { continue }
      if ("$got" -ne "$want") { $bad += "$($v[0]): $got != $want" }
    }
    "{0}: {1} cells written, {2} differ{3}" -f [IO.Path]::GetFileName($p), $s.values.Count, $bad.Count, $(if ($bad.Count) { ' | ' + ($bad -join '; ') } else { '' })
    $wb.Close($false)
  }
} finally {
  $h.Quit()
  [void][Runtime.InteropServices.Marshal]::ReleaseComObject($h)
}
