param(
  [Parameter(Mandatory=$true)][string]$Pattern,
  [int]$Max = 40,
  [int]$Types = 3
)
$dump = 'F:\DeepSeek Work Part\TIA-MCP\docs\openness-api.txt'
$lines = Get-Content $dump
$hits = @()
for ($i = 0; $i -lt $lines.Count; $i++) {
  if ($lines[$i] -match "^### .*$Pattern") { $hits += $i }
}
if ($hits.Count -eq 0) { "no type matched '$Pattern'"; exit 0 }

foreach ($i in ($hits | Select-Object -First $Types)) {
  $end = $i + 1
  while ($end -lt $lines.Count -and $lines[$end] -notmatch '^### ') { $end++ }
  "=== $($lines[$i]) ==="
  $lines[($i+1)..($end-1)] | Where-Object { $_ -match '^\s+(PROP|METH|CTOR|ENUM)' } | Select-Object -First $Max
  ""
}
