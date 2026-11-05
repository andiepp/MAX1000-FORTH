# Builds the kernel image and the FPGA configuration (build/output_files/max1000_forth.sof/.pof).
#   -PullUps   weak pull-ups on D0..D14 and PIO1..PIO8 (fixed in the configuration)
param(
    [string]$Quartus = $env:QUARTUS_ROOTDIR,
    [switch]$PullUps
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if ($Quartus -and -not (Test-Path (Join-Path $Quartus 'bin64\quartus_sh.exe'))) { $Quartus = $null }
if (-not $Quartus) {
    $Quartus = Get-PSDrive -PSProvider FileSystem | ForEach-Object { Join-Path $_.Root 'Quartus\18.1\quartus' } |
        Where-Object { Test-Path (Join-Path $_ 'bin64\quartus_sh.exe') } | Select-Object -First 1
}
if (-not $Quartus) { throw 'Quartus not found: pass -Quartus <...\quartus> or set QUARTUS_ROOTDIR' }

Push-Location $root
try {
    python tools/hf16.py build forth/kernel.fs -o build/forth
    if ($LASTEXITCODE) { throw 'kernel build failed' }
    $env:MAX1000_PULLUPS = if ($PullUps) { '1' } else { '0' }
    & (Join-Path $Quartus 'bin64\quartus_sh.exe') -t scripts/build.tcl *> build/quartus.log
    $ok = $LASTEXITCODE -eq 0
    Remove-Item Env:MAX1000_PULLUPS -ErrorAction SilentlyContinue
    Select-String -Path build/quartus.log -Pattern '^Error|Critical Warning \((?!16248|16562)|BUILD OK' | ForEach-Object Line
    if (-not $ok) { throw 'Quartus build failed, see build/quartus.log' }
    Select-String -Path build/output_files/max1000_forth.sta.summary -Pattern 'Type|Slack' | Select-Object -First 8 | ForEach-Object Line
    Select-String -Path build/output_files/max1000_forth.fit.summary -Pattern 'Total logic|Total memory|Embedded Multiplier|Total pins' | ForEach-Object Line
} finally {
    Pop-Location
}
