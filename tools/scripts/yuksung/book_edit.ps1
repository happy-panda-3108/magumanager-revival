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
  # 2026-11-11 (후보 블록 below the 마무리): "insert_rows": [[row, count], ...] inserts whole rows before `row` (the notes
  # under the blocks move down), then "copy": [[srcRange, dstCell], ...] copies a block (formulas, formats) there.
  foreach ($ins in $s.insert_rows) { $ws.Range("$($ins[0]):$($ins[0] + $ins[1] - 1)").EntireRow.Insert() | Out-Null }
  foreach ($cp in $s.copy) { $ws.Range($cp[0]).Copy($ws.Range($cp[1])) | Out-Null }
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
  # 2026-11-15 (FA 추천): "add_sheet": { "name", "values": [[addr, value]...], "fills", "fonts", "bold": [range...],
  # "widths": [[col, width]...], "wrap": [range...] } adds (or empties and rewrites) a sheet after the last one; the user's
  # own sheets stay as they are.
  if ($null -ne $s.add_sheet) {
    $a = $s.add_sheet
    $new = $null
    foreach ($w in $wb.Worksheets) { if ($w.Name -eq $a.name) { $new = $w } }
    if ($null -eq $new) {
      $new = $wb.Worksheets.Add([Type]::Missing, $wb.Worksheets.Item($wb.Worksheets.Count))
      $new.Name = $a.name
    } else { $new.Cells.Clear() | Out-Null }
    foreach ($v in $a.values) {
      $cell = $new.Range($v[0])
      if ($null -eq $v[1]) { continue }
      elseif ($v[1] -is [string] -and $v[1].StartsWith('=')) { $cell.Formula = $v[1] }
      elseif ($v[1] -is [string]) { $cell.Value2 = [string]$v[1] }
      else { $cell.Formula = [string]$v[1] }
    }
    foreach ($w in $a.widths) { $new.Columns.Item($w[0]).ColumnWidth = $w[1] }
    foreach ($f in $a.fills) { $new.Range($f[0]).Interior.Color = (Ole $f[1]) }
    foreach ($f in $a.fonts) { $new.Range($f[0]).Font.Color = (Ole $f[1]) }
    foreach ($b in $a.bold) { $new.Range($b).Font.Bold = $true }
    foreach ($r in $a.wrap) { $new.Range($r).WrapText = $true }
    # "formats": [[range, "0.00"], ...] number formats (한셀 turns "7.40" into 7.4; the format keeps two decimals on show).
    foreach ($nf in $a.formats) { $new.Range($nf[0]).NumberFormat = [string]$nf[1] }
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
    if ($null -ne $s.add_sheet) {
      $ns = $wb.Worksheets.Item($s.add_sheet.name)
      foreach ($v in $s.add_sheet.values) {
        if ($null -eq $v[1] -or ($v[1] -is [string] -and $v[1].StartsWith('='))) { continue }
        $got = $ns.Range($v[0]).Value2
        $gn = 0.0; $wn = 0.0
        if ([double]::TryParse("$got", [ref]$gn) -and [double]::TryParse("$($v[1])", [ref]$wn) -and [math]::Abs($gn - $wn) -lt 1e-9) { continue }
        if ("$got" -ne "$($v[1])") { $bad += "$($s.add_sheet.name)!$($v[0]): $got != $($v[1])" }
      }
    }
    $n = $s.values.Count + $(if ($null -ne $s.add_sheet) { $s.add_sheet.values.Count } else { 0 })
    "{0}: {1} cells written, {2} differ{3}" -f [IO.Path]::GetFileName($p), $n, $bad.Count, $(if ($bad.Count) { ' | ' + ($bad -join '; ') } else { '' })
    $wb.Close($false)
  }
} finally {
  $h.Quit()
  [void][Runtime.InteropServices.Marshal]::ReleaseComObject($h)
}
