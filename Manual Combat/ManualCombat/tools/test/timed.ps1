# timed.ps1 -Mode block|attack -Before 6,3,...: for each value, wait for a swing at Su, press when its blow lands in that many ticks or fewer
# (block: aimed at the blow's side; attack: from the same side, mirrored aim), step until it is over, print what happened.
[CmdletBinding(PositionalBinding = $false)]
param([int[]]$Before = @(5), [string]$Mode = "block", [int]$Rest = 60)
$s = Split-Path -Parent $MyInvocation.MyCommand.Path
function Rep { (& "$s\mc.ps1" report) -replace '^.*?; tick', 'tick' }
function Cs { (& "$s\c.ps1" combat-status | ConvertFrom-Json).data.blows30 }
function Field($r, $name) { if ($r -match "$name (\w+)") { $Matches[1] } else { "" } }
function LandsIn($r) { if ($r -match 'lands in (-?\d+)') { [int]$Matches[1] } else { $null } }
foreach ($k in $Before) {
    $null = & "$s\mc.ps1" "block off"
    $side = ""; $left = $null
    for ($t = 0; $t -lt 400; $t++) {
        $null = & "$s\c.ps1" -Timeout 10 step 1
        $r = Rep; $inc = Field $r 'incoming'
        # 'code 7/9': a blow from behind, not aimable: wait for one from the front
        if ($inc -and $inc -ne 'none' -and $inc -ne 'code') { $side = $inc; $left = LandsIn $r; if ($left -ne $null -and $left -le $k) { break } }
    }
    if (-not $side) { "before $k : no swing came in 400 ticks"; continue }
    $cs0 = Cs; $r0 = Rep
    $p0 = Field $r0 'parries'; $c0 = Field $r0 'chambers'; $k0 = Field $r0 'clashes'
    $press = switch ($Mode) {
        'block' { $side }
        'other' { if ($side -eq 'overhead') { 'thrust' } else { 'overhead' } }   # a swing from another side: no chamber or clash (hyper armor)
        'dodge' { '' }
        'mirrored' { switch ($side) { 'left' { 'right' } 'right' { 'left' } default { $side } } }   # the opposite side: must NOT chamber
        default { $side }   # attack: aimed as the block (0.9.9 chamber rule)
    }
    $null = switch ($Mode) {
        'block' { & "$s\mc.ps1" "aim $press" "block on $press" }
        'dodge' { & "$s\mc.ps1" "dodge" }
        default { & "$s\mc.ps1" "aim $press" "attack $press" }
    }
    for ($t = 0; $t -lt 90; $t++) { $null = & "$s\c.ps1" -Timeout 10 step 1; if ((Field (Rep) 'incoming') -eq 'none') { break } }
    $null = & "$s\c.ps1" -Timeout 10 step 3
    $after = Cs; $r1 = Rep
    "pressed $Mode $press with the $side blow landing in $left ticks -> blocks +$($after.blocks - $cs0.blocks), hits taken +$($after.takenHits - $cs0.takenHits), " +
        "parries $p0->$(Field $r1 'parries'), chambers $c0->$(Field $r1 'chambers'), clashes $k0->$(Field $r1 'clashes'), " +
        "dodges +$($after.dodges - $cs0.dodges) evaded +$($after.evaded - $cs0.evaded), poise $(Field $r1 'poise'), now $(Field $r1 'Su #0:')"
    $null = & "$s\mc.ps1" "block off"
    $null = & "$s\c.ps1" -Timeout 10 step $Rest
    $null = & "$s\c.ps1" heal
}

