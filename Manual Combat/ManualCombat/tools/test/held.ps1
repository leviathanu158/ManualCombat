# held.ps1 -Hold n [-Dir overhead]: hold the attack n ticks (hold to ready), then let go; prints the swing's clip and the enemy's state.
[CmdletBinding(PositionalBinding = $false)]
param([int]$Hold = 40, [string]$Dir = "overhead", [int]$Every = 5)
$s = Split-Path -Parent $MyInvocation.MyCommand.Path
function Rep { (& "$s\mc.ps1" report) -replace '^.*?; tick', 'tick' }
function Cs { (& "$s\c.ps1" combat-status | ConvertFrom-Json).data.blows30 }
function Bit($r) { $sw = if ($r -match 'swing ([^,]+, blow \d|none)') { $Matches[1] } else { "?" }
    $en = if ($r -match 'enemies: ([^;]*)') { $Matches[1] } else { "" }
    $h = if ($r -match 'attack held (\w+( \(readied\))?)') { $Matches[1] } else { "" }
    "held $h | swing $sw | $en" }
for ($t = 0; $t -lt 200; $t++) {
    $r = Rep
    if ($r -match 'Su #0: free' -and $r -match 'incoming none') { break }
    $null = & "$s\c.ps1" -Timeout 10 step 1
}
$c0 = Cs
$null = & "$s\mc.ps1" "aim $Dir" "attack hold $Dir"
for ($t = 1; $t -le $Hold; $t++) {
    $null = & "$s\c.ps1" -Timeout 10 step 1
    if ($t % $Every -eq 0) { "t+$t " + (Bit (Rep)) }
}
$null = & "$s\mc.ps1" "release"
"-- released"
for ($t = 1; $t -le 45; $t++) {
    $null = & "$s\c.ps1" -Timeout 10 step 1
    if ($t % $Every -eq 0) { "r+$t " + (Bit (Rep)) }
}
$c1 = Cs
"thrown +$($c1.thrown - $c0.thrown), landed +$($c1.landed - $c0.landed), hits taken +$($c1.takenHits - $c0.takenHits)"
