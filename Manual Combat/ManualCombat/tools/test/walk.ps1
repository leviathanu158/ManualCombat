# walk.ps1 -Way left: hold a swing, step with the footwork while it is held (screenshots), then let go while still stepping.
param([string]$Way = "left", [string]$Dir = "overhead", [int]$Hold = 30)
$s = Split-Path -Parent $MyInvocation.MyCommand.Path
function Rep { (& "$s\mc.ps1" report) -replace '^.*?; tick', 'tick' }
function Pos { $o = & "$s\c.ps1" observe --brief; if ($o -match '"pos":\[(-?[\d.]+),(-?[\d.]+)') { "{0:0},{1:0}" -f [double]$Matches[1], [double]$Matches[2] } }
function Sw { $r = Rep; $sw = if ($r -match 'swing ([^,]+, blow \d|none)') { $Matches[1] }; $h = if ($r -match 'attack held ([^,]+)') { $Matches[1] }; "$h | $sw | at $(Pos)" }
for ($t = 0; $t -lt 200; $t++) { $r = Rep; if ($r -match 'Su #0: free' -and $r -match 'incoming none') { break }; $null = & "$s\c.ps1" -Timeout 10 step 1 }
"start at $(Pos)"
$null = & "$s\mc.ps1" "aim $Dir" "attack hold $Dir"
& "$s\c.ps1" -Timeout 10 step 4 | Out-Null
$null = & "$s\mc.ps1" "move $Way"
for ($t = 1; $t -le $Hold; $t++) {
  $null = & "$s\c.ps1" -Timeout 10 step 1
  if ($t % 5 -eq 0) { "held t+$t : $(Sw)" }
  if ($t -eq 20) { & "$s\c.ps1" screenshot C:\Kenshi_Meitou\ctl\walk_held.png | Out-Null }
}
$null = & "$s\mc.ps1" "release"
for ($t = 1; $t -le 30; $t++) {
  $null = & "$s\c.ps1" -Timeout 10 step 1
  if ($t % 5 -eq 0) { "released t+$t : $(Sw)" }
  if ($t -eq 4) { & "$s\c.ps1" screenshot C:\Kenshi_Meitou\ctl\walk_strike.png | Out-Null }
}
$null = & "$s\mc.ps1" "move off"
