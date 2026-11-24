# spam.ps1: hold an attack and switch sides every 6 ticks for N ticks; count the enemy's swings at Su meanwhile.
param([int]$Ticks = 240)
$s = Split-Path -Parent $MyInvocation.MyCommand.Path
function Rep { (& "$s\mc.ps1" report) -replace '^.*?; tick', 'tick' }
for ($t = 0; $t -lt 200; $t++) { $r = Rep; if ($r -match 'Su #0: free' -and $r -match 'incoming none') { break }; $null = & "$s\c.ps1" -Timeout 10 step 1 }
$null = & "$s\mc.ps1" "aim left" "attack hold left"
$sides = @('right', 'left', 'overhead', 'thrust')
$swingTicks = 0; $starts = 0; $was = $false
for ($t = 1; $t -le $Ticks; $t++) {
  if ($t % 6 -eq 0) { $null = & "$s\mc.ps1" "reaim $($sides[($t / 6) % 4])" }
  $null = & "$s\c.ps1" -Timeout 10 step 1
  $r = Rep
  $now = $r -match 'enemies: [^:]+: swinging'
  if ($now) { $swingTicks++; if (-not $was) { $starts++ } }
  $was = $now
}
$null = & "$s\mc.ps1" "release"
"held and switched every 6 ticks for $Ticks ticks: the enemy started $starts swings at Su (swinging $swingTicks ticks)"
