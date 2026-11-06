<#
.SYNOPSIS
    Writes MAX1000-FORTH into the MAX1000's FPGA over its USB cable (Arrow USB Blaster).

.DESCRIPTION
    Without -Sram: programs max1000_forth.pof into the MAX 10's internal flash; the board
    starts MAX1000-FORTH at every power-up (press the board's Reset button or replug USB
    to start it now).
    With -Sram: loads max1000_forth.sof into the FPGA only, until power-off.

    Needs quartus_pgm (Quartus Prime Lite / Standard, or the stand-alone Quartus Prime
    Programmer) and the Arrow USB Blaster driver. The script looks for quartus_pgm in
    -Quartus, QUARTUS_ROOTDIR, on the PATH, and in the usual install folders on every drive.
    The .pof/.sof are taken from the script's folder (the release's firmware folder) or
    from build\output_files (a repository build).

.EXAMPLE
    .\firmware\program_flash.ps1
    .\firmware\program_flash.ps1 -Sram
#>
[CmdletBinding()]
param(
    [switch]$Sram,
    [string]$Quartus = $env:QUARTUS_ROOTDIR,
    [string]$Cable = '1'
)
$ErrorActionPreference = 'Stop'

function Find-Pgm {
    if ($Quartus) {
        foreach ($p in (Join-Path $Quartus 'bin64\quartus_pgm.exe'), (Join-Path $Quartus 'quartus_pgm.exe')) {
            if (Test-Path $p) { return $p }
        }
    }
    $cmd = Get-Command quartus_pgm -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($drive in (Get-PSDrive -PSProvider FileSystem).Root) {
        foreach ($pattern in 'intelFPGA*\*\quartus\bin64', 'intelFPGA*\*\qprogrammer\bin64',
                             'altera*\*\quartus\bin64', 'altera*\*\qprogrammer\bin64', 'Quartus\*\quartus\bin64') {
            $hit = Resolve-Path (Join-Path $drive "$pattern\quartus_pgm.exe") -ErrorAction SilentlyContinue |
                Sort-Object Path -Descending | Select-Object -First 1
            if ($hit) { return $hit.Path }
        }
    }
    throw 'quartus_pgm not found: install the Quartus Prime Programmer, or pass -Quartus <...\quartus>'
}

$name = if ($Sram) { 'max1000_forth.sof' } else { 'max1000_forth.pof' }
$file = @((Join-Path $PSScriptRoot $name), (Join-Path $PSScriptRoot "..\build\output_files\$name")) |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $file) { throw "$name not found next to the script or in build\output_files" }
$file = (Resolve-Path $file).Path

$pgm = Find-Pgm
$jtagconfig = Join-Path (Split-Path $pgm) 'jtagconfig.exe'
if (Test-Path $jtagconfig) { & $jtagconfig }
Write-Host "programming $file with $pgm"
& $pgm -c $Cable -m JTAG -o "p;$file"
if ($LASTEXITCODE) { throw 'programming failed (is the board connected and the Arrow USB Blaster driver installed?)' }
if ($Sram) {
    Write-Host 'MAX1000-FORTH runs until power-off.'
} else {
    Write-Host 'Done. Press the board''s Reset button (or replug USB) to start MAX1000-FORTH from flash.'
}
