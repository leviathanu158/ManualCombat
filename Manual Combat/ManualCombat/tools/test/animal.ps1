# animal.ps1: a hostile bonedog by Su; step N ticks and print its swings (lands in) and the mod's animal lines.
param([int]$Ticks = 300)
$s = Split-Path -Parent $MyInvocation.MyCommand.Path
$log = "C:\Kenshi_Meitou\versions\0.22.2-nightly.20261122.2\log.txt"
function Rep { (& "$s\mc.ps1" report) -replace '^.*?; tick', 'tick' }
$o = & "$s\c.ps1" observe --brief
if ($o -match '"pos":\[(-?[\d.]+),(-?[\d.]+)') { $x=[double]$Matches[1]; $z=[double]$Matches[2] }
$r = & "$s\c.ps1" -Timeout 20 spawn-at ($x+60) $z Bonedog --faction hostile --aggro; $id = if ($r -match '"id":"(c:[\d.]+)"') { $Matches[1] }
"bonedog $id"
& "$s\c.ps1" select c:0.0 | Out-Null; & "$s\c.ps1" order attack $id | Out-Null
$start = (Get-Content $log | Measure-Object -Line).Lines
$was = ""
for ($t = 1; $t -le $Ticks; $t++) {
  $null = & "$s\c.ps1" -Timeout 10 step 1
  $r = Rep; $inc = if ($r -match '(incoming [^,]+)') { $Matches[1] } else { "" }
  if ($inc -ne 'incoming none' -and $inc -ne $was) { "t+$t $inc" }
  $was = $inc
  if ($t % 60 -eq 0) { & "$s\c.ps1" heal | Out-Null }
}
Get-Content $log | Select-Object -Skip $start | Select-String 'manualcombat: (animal|HIT TAKEN)' | Select -First 14 | % { $_.Line -replace '^mod\s+manualcombat: ','' }
$id | Out-File -Encoding ascii "$s\animal.txt"
