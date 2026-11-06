# Runs the unit testbench of the serial interfaces (sim/tb_serial.v: rtl/uart_port.v,
# spi_port.v, i2c_port.v) in the Vivado simulator (the modules are plain Verilog).
# Usage (from the repository root): .\sim\run_serial.ps1 [-Vivado <Vivado folder>\bin]
param(
    [string]$Vivado
)
$ErrorActionPreference = 'Stop'
# Vivado 2023.2's bin folder: -Vivado, else $env:XILINX_VIVADO, else \Xilinx\Vivado\2023.2 on any drive
if (-not $Vivado) {
    $Vivado = @($env:XILINX_VIVADO) + (Get-PSDrive -PSProvider FileSystem | ForEach-Object { Join-Path $_.Root 'Xilinx\Vivado\2023.2' }) |
        Where-Object { $_ -and (Test-Path (Join-Path $_ 'bin\xvlog.bat')) } | ForEach-Object { Join-Path $_ 'bin' } | Select-Object -First 1
    if (-not $Vivado) { throw 'Vivado 2023.2 not found: pass -Vivado <Vivado folder>\bin' }
}
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'build\sim_serial'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out | Out-Null
$rtl = 'fifo_bram.v', 'uart_port.v', 'spi_port.v', 'i2c_port.v' | ForEach-Object { Join-Path $root "rtl\$_" }

Push-Location $out
try {
    & "$Vivado\xvlog.bat" -nolog $rtl (Join-Path $root 'sim\tb_serial.v') | Select-String 'ERROR|WARNING'
    & "$Vivado\xelab.bat" -nolog -debug off -timescale 1ns/1ps tb_serial -s serial_snap | Select-String 'ERROR'
    $lines = & "$Vivado\xsim.bat" serial_snap -nolog -R |
        Where-Object { $_ -notmatch '^(INFO|\*\*\*\*|Vivado Simulator|Time resolution|  \*\*|source |# |ECHO)' }
    $lines
    if (-not ($lines -match 'checks, 0 failed')) { exit 1 }
}
finally {
    Pop-Location
}
