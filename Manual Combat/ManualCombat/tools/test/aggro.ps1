# aggro.ps1: Su is ordered to attack the first id; prints every bandit's fight state every N ticks.
param([string[]]$Ids, [int]$Ticks = 300, [int]$Every = 15, [switch]$Swing)
$s = Split-Path -Parent $MyInvocation.MyCommand.Path
function St($id) {
  $j = (& "$s\c.ps1" combat-status --id $id | ConvertFrom-Json)
  if (-not $j.ok) { return "$id ?" }
  $d = $j.data
  "{0}: {1}, task {2}, inCombat {3}, target {4} {5}" -f $d.name, $d.status, $d.task, $d.inCombat, $d.target.name, $(if ($d.target) { "{0:0}" -f $d.target.dist })
}
& "$s\c.ps1" select c:0.0 | Out-Null
& "$s\c.ps1" order attack $Ids[0] | Out-Null
for ($t = 1; $t -le $Ticks; $t++) {
  $null = & "$s\c.ps1" -Timeout 10 step 1
  if ($Swing -and $t % 40 -eq 0) { $null = & "$s\mc.ps1" "attack overhead" }
  if ($t % $Every -eq 0) { "--- t+$t  Su: $(St 'c:0.0')"; foreach ($id in $Ids) { "   " + (St $id) } }
}
