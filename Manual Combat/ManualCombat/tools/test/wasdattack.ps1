# wasdattack.ps1 [-NoMoves]: next to the starving bandit mob, attack Slints while "walking" the way PZA's WASD does (single-character move orders
# every 5 ticks for 30 ticks, the first in the same tick as the attack order); then watch the group.
param([switch]$NoMoves)
$s = Split-Path -Parent $MyInvocation.MyCommand.Path
function St($id) { $d = (& "$s\c.ps1" combat-status --id $id | ConvertFrom-Json).data; "{0}: {1}, task {2}, target {3}" -f $d.name, $d.status, $d.task, $d.target.name }
& "$s\c.ps1" select c:0.0 | Out-Null
$n = (& "$s\c.ps1" observe --radius 900 --full | ConvertFrom-Json).data.nearby
$group = @($n.neutrals) + @($n.threats) | ? { $_.faction -eq 'Starving Bandits' }
$ids = @($group | % { $_.id })
$slints = $group | ? { $_.name -eq 'Slints' } | Select-Object -First 1
if (-not $slints) { "no Slints near: $($group.name -join ', ')"; return }
"group: $($group.name -join ', '); Slints $($slints.id) at $($slints.pos)"
# onto the group itself (their spot is on the ground)
& "$s\c.ps1" -Timeout 30 tp ($slints.pos[0] + 4) ($slints.pos[1]) | Out-Null
& "$s\c.ps1" -Timeout 10 step 5 | Out-Null
$o = & "$s\c.ps1" observe --brief; if ($o -match '"pos":\[(-?[\d.]+),(-?[\d.]+)') { $x=[double]$Matches[1]; $z=[double]$Matches[2] }
& "$s\c.ps1" order attack $slints.id | Out-Null
for ($k = 0; $k -lt 6; $k++) {
  if (-not $NoMoves) { & "$s\c.ps1" order move ($x - 5 - $k) $z --id c:0.0 | Out-Null }
  & "$s\c.ps1" -Timeout 10 step 5 | Out-Null
}
foreach ($t in 1..3) {
  & "$s\c.ps1" -Timeout 10 step 40 | Out-Null
  "--- +$($t*40+30) ticks  Su: $(St 'c:0.0')"
  foreach ($id in $ids) { "   " + (St $id) }
}
