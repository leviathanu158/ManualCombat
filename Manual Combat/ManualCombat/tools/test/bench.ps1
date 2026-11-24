# bench.ps1 -Name n [-NoMod]: Meitou's play benchmark on mctest (segments settle, idle, orbit, fight), hidden window; results in scratchpad\bench\<n>.
param([string]$Name, [switch]$NoMod, [string]$Only = "settle,idle,orbit,fight")
$s = Split-Path -Parent $MyInvocation.MyCommand.Path
$v = "C:\Kenshi_Meitou\versions\0.22.2-nightly.20261122.2"
$out = Join-Path $s "bench\$Name"
New-Item -ItemType Directory -Force $out | Out-Null
$env:KENSHI_PATH = "C:\Steam Games\steamapps\common\Kenshi"
$env:MEITOU_BENCH_ONLY = $Only
$cfgArgs = @()
if ($NoMod) {
  $cfg = Join-Path $s "mods-nomc.cfg"
  # no byte-order mark (it would break the first mod's name)
  [IO.File]::WriteAllLines($cfg, [string[]](Get-Content "$env:KENSHI_PATH\data\mods.cfg" | Where-Object { $_ -ne "ManualCombat.mod" }))
  $cfgArgs = @("--mods-cfg", "`"$cfg`"")
}
$t0 = Get-Date
$p = Start-Process -FilePath "$v\MeitouClient.exe" -WorkingDirectory "$env:LOCALAPPDATA\Meitou" -PassThru -Wait `
  -ArgumentList (@("--hidden-window", "--load", "`"$env:LOCALAPPDATA\Meitou\save\mctest`"", "--play-bench", "`"$out`"") + $cfgArgs)
"${Name}: exit $($p.ExitCode) after $([int]((Get-Date) - $t0).TotalSeconds) s"
if (Test-Path "$out\summary.md") { Get-Content "$out\summary.md" | Select-Object -First 40 }
