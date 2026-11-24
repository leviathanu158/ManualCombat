# start.ps1: start Meitou on mctest with the agent channel and the mod's test channel; wait for the load, pause, settle, report
$s = Split-Path -Parent $MyInvocation.MyCommand.Path
$D = "C:\Kenshi_Meitou\ctl"; Remove-Item "$D\cmd", "$D\out", "$D\mc.cmd", "$D\mc.out" -ErrorAction SilentlyContinue
$v = "C:\Kenshi_Meitou\versions\0.22.2-nightly.20261122.2"
$env:KENSHI_PATH = "C:\Steam Games\steamapps\common\Kenshi"; $env:MANUALCOMBAT_TEST_DIR = $D
# working directory as the launcher's: Meitou reads meitou.user.json (the player's settings) from it
$p = Start-Process -FilePath "$v\MeitouClient.exe" -WorkingDirectory "$env:LOCALAPPDATA\Meitou" -ArgumentList @("--windowed", "--load", "`"$env:LOCALAPPDATA\Meitou\save\mctest`"", "--control-dir", "`"$D`"") -PassThru
$p.Id | Out-File -Encoding ascii "$D\game.pid"; "pid $($p.Id)"
Start-Sleep 5
& "$s\c.ps1" -Timeout 430 wait-for loaded=true timeout=420
# the mod must have started: stop here if it did not (a failed start leaves the test channel silent)
$started = Get-Content "$v\log.txt" | Select-String 'manualcombat(: ready|: not loaded)| ManualCombat: not loaded' | Select-Object -Last 1
if ($started -notmatch 'ready') { "MOD DID NOT START: $started"; Get-Content "$v\log.txt" | Select-String 'ManualCombat: ' | Select-Object -Last 3 | ForEach-Object Line; exit 1 }
& "$s\c.ps1" pause
& "$s\c.ps1" -Timeout 30 step 300
& "$s\mc.ps1" report

