# Mod test channel client: mc.ps1 "line1" "line2" ... ; prints the result lines
[CmdletBinding(PositionalBinding = $false)]
param([double]$Timeout = 5, [Parameter(ValueFromRemainingArguments = $true)][string[]]$Lines)
$D = "C:\Kenshi_Meitou\ctl"
$out = "$D\mc.out"
$before = if (Test-Path $out) { @(Get-Content $out).Count } else { 0 }
$t0 = Get-Date
while (Test-Path "$D\mc.cmd") { if (((Get-Date) - $t0).TotalSeconds -gt $Timeout) { "TIMEOUT waiting for mc.cmd slot"; exit 2 }; Start-Sleep -Milliseconds 50 }
$tmp = "$D\.mc$PID"
[System.IO.File]::WriteAllLines($tmp, $Lines)
Move-Item $tmp "$D\mc.cmd"
$t0 = Get-Date
while (((Get-Date) - $t0).TotalSeconds -lt $Timeout) {
    if (Test-Path $out) {
        $all = @(Get-Content $out)
        if ($all.Count -ge $before + $Lines.Count) { $all[$before..($all.Count - 1)]; exit 0 }
    }
    Start-Sleep -Milliseconds 50
}
"TIMEOUT (mc: $($Lines -join ' | '))"; exit 2
