# 2026-11-07 (user: "육성표를 마이크로소프트 엑셀로 만들어줬는데 한셀 2024로 다시 만들어줄래?"): opens each workbook in a hidden
# 한셀 2024 (HCell.Application), recalculates, and saves it as a 한셀 document (.cell) and as a 한셀-written .xlsx in <out dir>.
# Then reopens every output and prints the values of a few cells next to the source's, plus validation / conditional
# format / comment counts, so a broken conversion shows up. Never run it on a file the user has open in 한셀.
param([Parameter(Mandatory = $true)][string]$OutDir, [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)][string[]]$Paths)
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$probe = 'C7', 'C8', 'C11', 'C12', 'K11', 'O12', 'S3', 'S5', 'B104'

function Describe($h, $path) {
  $wb = $h.Workbooks.Open($path)
  try {
    $ws = $wb.Worksheets.Item('육성 계산기')
    $vals = $probe | ForEach-Object { "$_=$($ws.Range($_).Value2)".Substring(0, [Math]::Min(40, "$_=$($ws.Range($_).Value2)".Length)) }
    $val = 0; try { $val = $ws.Cells.SpecialCells(-4174).Count } catch {}
    $fc = 0; try { $fc = $ws.Cells.FormatConditions.Count } catch {}
    "sheets=$($wb.Worksheets.Count) comments=$($ws.Comments.Count) validation=$val condformats=$fc | $($vals -join ' | ')"
  } finally { $wb.Close($false) }
}

$h = New-Object -ComObject HCell.Application
try {
  $h.DisplayAlerts = $false
  foreach ($p in $Paths) {
    $full = (Resolve-Path $p).Path
    $base = [IO.Path]::GetFileNameWithoutExtension($full)
    $wb = $h.Workbooks.Open($full)
    $h.CalculateFull()
    $wb.Saved = $false
    $wb.SaveAs((Join-Path $OutDir "$base.cell"))
    $wb.Close($false)
    $wb = $h.Workbooks.Open($full)
    $h.CalculateFull()
    $wb.Saved = $false
    $wb.SaveAs((Join-Path $OutDir "$base.xlsx"), 51)
    $wb.Close($false)
    "== $base"
    "  src   $(Describe $h $full)"
    "  cell  $(Describe $h (Join-Path $OutDir "$base.cell"))"
    "  xlsx  $(Describe $h (Join-Path $OutDir "$base.xlsx"))"
  }
} finally {
  $h.Quit()
  [void][Runtime.InteropServices.Marshal]::ReleaseComObject($h)
}
