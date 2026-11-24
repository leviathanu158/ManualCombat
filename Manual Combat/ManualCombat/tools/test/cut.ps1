# cut.ps1 -With block|step|none: an attack; once its blow has struck, block (or a W step) and see whether the follow-through is cut.
param([string]$With = "block")
$s = Split-Path -Parent $MyInvocation.MyCommand.Path
function Rep { (& "$s\mc.ps1" report) -replace '^.*?; tick', 'tick' }
for ($t = 0; $t -lt 200; $t++) { $r = Rep; if ($r -match 'Su #0: free' -and $r -match 'incoming none') { break }; $null = & "$s\c.ps1" -Timeout 10 step 1 }
$null = & "$s\mc.ps1" "attack left"
$struck = -1
for ($t = 1; $t -le 90; $t++) {
  $null = & "$s\c.ps1" -Timeout 10 step 1
  $r = Rep; $sw = if ($r -match 'swing ([^,]+, blow \d|none)') { $Matches[1] } else { "?" }
  if ($struck -lt 0 -and $sw -match 'blow 0') { $struck = $t; "blow struck at t+$t ($sw)"
    if ($With -eq 'block') { $null = & "$s\mc.ps1" "block on left" } }
  if ($struck -ge 0 -and ($t - $struck) % 3 -eq 0) { "t+$t swing $sw" }
  if ($struck -ge 0 -and $sw -eq 'none') { "swing over at t+$t ($($t - $struck) ticks after the blow)"; break }
}
$null = & "$s\mc.ps1" "block off"
