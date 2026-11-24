# fists.ps1: an unarmed hand-fought character against one bandit: dodges pressed N ticks before the blow, each followed by an attack.
[CmdletBinding(PositionalBinding = $false)]
param([int[]]$Before = @(3))
$s = Split-Path -Parent $MyInvocation.MyCommand.Path
function Rep { (& "$s\mc.ps1" report) -replace '^.*?; tick', 'tick' }
function Field($r, $name) { if ($r -match "$name (\w+)") { $Matches[1] } else { "" } }
function LandsIn($r) { if ($r -match 'lands in (-?\d+)') { [int]$Matches[1] } else { $null } }
function Cs { (& "$s\c.ps1" combat-status | ConvertFrom-Json).data.blows30 }
foreach ($k in $Before) {
    $null = & "$s\mc.ps1" "block off"
    $side = ""; $left = $null
    for ($t = 0; $t -lt 400; $t++) {
        $null = & "$s\c.ps1" -Timeout 10 step 1
        $r = Rep; $inc = Field $r 'incoming'
        # 'code 7/9': from behind, not aimable
        if ($inc -and $inc -ne 'none' -and $inc -ne 'code') { $side = $inc; $left = LandsIn $r; if ($left -ne $null -and $left -le $k) { break } }
    }
    if (-not $side) { "before $k : no swing came"; continue }
    $c0 = Cs
    $null = & "$s\mc.ps1" "aim $side" "block on $side"
    for ($t = 0; $t -lt 60; $t++) { $null = & "$s\c.ps1" -Timeout 10 step 1; if ((Field (Rep) 'incoming') -eq 'none') { break } }
    $c1 = Cs
    $null = & "$s\mc.ps1" "block off"
    $null = & "$s\c.ps1" -Timeout 10 step 2
    $state0 = Field (Rep) 'Su #0:'
    $null = & "$s\mc.ps1" "aim overhead" "attack overhead"
    $swingAt = -1
    for ($t = 1; $t -le 30; $t++) { $null = & "$s\c.ps1" -Timeout 10 step 1; if ((Field (Rep) 'Su #0:') -eq 'swinging') { $swingAt = $t; break } }
    "dodge pressed $left ticks before a $side blow -> evaded +$($c1.evaded - $c0.evaded), dodges +$($c1.dodges - $c0.dodges), hits taken +$($c1.takenHits - $c0.takenHits); " +
        "attack pressed while $state0 -> " + $(if ($swingAt -ge 0) { "swinging after $swingAt ticks" } else { "no swing within 1 s" })
    $null = & "$s\c.ps1" -Timeout 10 step 60
    $null = & "$s\c.ps1" heal
}
