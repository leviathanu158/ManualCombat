# xbwalk.ps1 -Reload: walk left for 60 ticks with the crossbow out (aiming), or reloading; prints distance, speed, report bits; screenshot.
param([switch]$Reload, [string]$Shot = "C:\Kenshi_Meitou\ctl\xbwalk.png")
$s = Split-Path -Parent $MyInvocation.MyCommand.Path
function Pos { $o = & "$s\c.ps1" observe --brief; if ($o -match '"pos":\[(-?[\d.]+),(-?[\d.]+)') { [double]$Matches[1], [double]$Matches[2] } }
function Bits { $r = (& "$s\mc.ps1" report) -replace '^.*?; tick', 'tick'; if ($r -match '(crossbow [^,]+(, spread [^)]+\))?), (reload [^,]+)') { "$($Matches[1]), $($Matches[3])" } else { $r.Substring(0, 120) } }
"start: $(Bits)"
if ($Reload) { $null = & "$s\mc.ps1" "reload on" }
$null = & "$s\mc.ps1" "move left"
$a = Pos
for ($t = 1; $t -le 60; $t++) {
  $null = & "$s\c.ps1" -Timeout 10 step 1
  if ($t -eq 10) { "t+10: $(Bits)" }
  if ($t -eq 40) { & "$s\c.ps1" camera focus c:0.0 | Out-Null; & "$s\c.ps1" camera set --zoom 45 --pitch 12 | Out-Null; & "$s\c.ps1" screenshot $Shot | Out-Null }
}
$b = Pos
$null = & "$s\mc.ps1" "move off"
if ($Reload) { $null = & "$s\mc.ps1" "reload off" }
$d = [math]::Sqrt(($b[0]-$a[0])*($b[0]-$a[0]) + ($b[1]-$a[1])*($b[1]-$a[1]))
"walked {0:0.0} units in 60 ticks = {1:0.0} u/s; end: {2}" -f $d, ($d / 2), (Bits)
