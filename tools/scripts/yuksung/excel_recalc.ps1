# Opens each workbook in a hidden Excel, recalculates everything and saves (cached values for openpyxl/pandas readers).
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Paths)
$x = New-Object -ComObject Excel.Application
$x.Visible = $false
$x.DisplayAlerts = $false
try {
  foreach ($p in $Paths) {
    $full = (Resolve-Path $p).Path
    $wb = $x.Workbooks.Open($full)
    $x.CalculateFull()
    $wb.Save()
    $wb.Close($false)
    "recalc $full"
  }
} finally {
  $x.Quit()
  [void][Runtime.InteropServices.Marshal]::ReleaseComObject($x)
}
