# heldchamber.ps1 -Before k [-NoRelease]: hold an attack from a side that is NOT the coming blow's; when the blow is k ticks from landing, switch
# to the blow's side and let go (or keep holding); print chamber / hit counts.
param([int[]]$Before = @(6), [switch]$NoRelease)
$s = Split-Path -Parent $MyInvocation.MyCommand.Path
function Rep { (& "$s\mc.ps1" report) -replace '^.*?; tick', 'tick' }
function Field($r, $name) { if ($r -match "$name (\w+)") { $Matches[1] } else { "" } }
function LandsIn($r) { if ($r -match 'lands in (-?\d+)') { [int]$Matches[1] } else { $null } }
foreach ($k in $Before) {
  for ($t = 0; $t -lt 200; $t++) { $r = Rep; if ($r -match 'Su #0: free' -and $r -match 'incoming none') { break }; $null = & "$s\c.ps1" -Timeout 10 step 1 }
  $null = & "$s\mc.ps1" "aim thrust" "attack hold thrust"
  $side = ""; $left = $null
  for ($t = 0; $t -lt 300; $t++) {
    $null = & "$s\c.ps1" -Timeout 10 step 1
    $r = Rep; $inc = Field $r 'incoming'
    if ($inc -and $inc -ne 'none' -and $inc -ne 'code' -and $inc -ne 'thrust') { $side = $inc; $left = LandsIn $r; if ($left -ne $null -and $left -le $k) { break } }
  }
  if (-not $side) { "before $k : no swing came"; $null = & "$s\mc.ps1" "release"; continue }
  $c0 = Field (Rep) 'chambers'
  $null = & "$s\mc.ps1" "reaim $side"
  if (-not $NoRelease) { $null = & "$s\mc.ps1" "release" }
  for ($t = 0; $t -lt 30; $t++) { $null = & "$s\c.ps1" -Timeout 10 step 1; if ((Field (Rep) 'incoming') -eq 'none') { break } }
  & "$s\c.ps1" -Timeout 10 step 3 | Out-Null
  $r1 = Rep
  "switched to $side and $(if ($NoRelease) { 'kept holding' } else { 'let go' }) with the blow landing in $left ticks -> chambers $c0->$(Field $r1 'chambers')"
  $null = & "$s\mc.ps1" "release"
  & "$s\c.ps1" -Timeout 10 step 40 | Out-Null; & "$s\c.ps1" heal | Out-Null
}
