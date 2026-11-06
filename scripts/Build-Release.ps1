<#
.SYNOPSIS
    Builds a MAX1000-FORTH release: the FPGA configuration, the manual's PDF and the zip for
    a GitHub release.

.DESCRIPTION
    1. Runs the simulator tests (sim\run_flash_tests.ps1, sim\test_protocol.py, sim\test_serial.py).
    2. Builds the kernel and the FPGA configuration (scripts\build.ps1; -SkipFpga uses the
       existing build\output_files).
    3. Builds docs\Manual.pdf and docs\Implementation-Details.pdf (needs Python with 'markdown' and Microsoft Edge; skipped with
       -SkipPdf, then the committed PDFs are used).
    4. Tests and publishes the MAX1000-FORTH app (needs the .NET 10 SDK): app\Max1000Forth.exe,
       one self-contained program with docs\Manual.pdf embedded.
    5. Writes dist\MAX1000-FORTH-<version>.zip: firmware (pof, sof, program_flash.ps1), app,
       docs (Manual.pdf, Implementation-Details.pdf, design.md, images), tools (mforth.py, hf16.py), examples, README,
       CHANGELOG, LICENSE.

.EXAMPLE
    .\scripts\Build-Release.ps1 -Version 1.3.1
#>
[CmdletBinding()]
param(
    [string]$Version = '1.3.1',
    [switch]$SkipTests,
    [switch]$SkipPdf,
    [switch]$SkipFpga
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist "MAX1000-FORTH-$Version"

Push-Location $root
try {
    if (-not $SkipTests) {
        & (Join-Path $root 'sim\run_flash_tests.ps1')
        if ($LASTEXITCODE) { throw 'flash tests failed' }
        python sim\test_protocol.py
        if ($LASTEXITCODE) { throw 'protocol tests failed' }
        python sim\test_serial.py
        if ($LASTEXITCODE) { throw 'serial tests failed' }
    }
    if (-not $SkipFpga) { & (Join-Path $root 'scripts\build.ps1') }
    if (-not $SkipPdf) {
        python tools\manual_contents.py           # the manual's contents list from its headings
        if ($LASTEXITCODE) { throw 'manual contents failed' }
        foreach ($doc in 'manual', 'design') {
            python tools\manual_pdf.py $doc
            if ($LASTEXITCODE) { throw "$doc PDF failed" }
        }
    }

    # the app, after the PDFs: it embeds docs\Manual.pdf
    $appOut = Join-Path $root 'build\app-publish'
    if (-not $SkipTests) {
        dotnet test app\Max1000Forth.slnx
        if ($LASTEXITCODE) { throw 'app tests failed' }
    }
    if (Test-Path $appOut) { Remove-Item -Recurse -Force $appOut }
    dotnet publish app\src\ZynqForth\ZynqForth.csproj -c Release -r win-x64 --self-contained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version="$Version.0" -o $appOut
    if ($LASTEXITCODE) { throw 'app publish failed' }

    # the repository's own copies, so that GitHub's "Code > Download ZIP" works as well:
    # firmware\ (pof, sof, program_flash.ps1) and app\Max1000Forth.exe in its small form,
    # which needs the .NET 10 Desktop Runtime (the zip gets the self-contained one)
    Copy-Item build\output_files\max1000_forth.pof, build\output_files\max1000_forth.sof, scripts\program_flash.ps1 `
        (Join-Path $root 'firmware')
    $appFd = Join-Path $root 'build\app-fd'
    if (Test-Path $appFd) { Remove-Item -Recurse -Force $appFd }
    dotnet publish app\src\ZynqForth\ZynqForth.csproj -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true -p:Version="$Version.0" -o $appFd
    if ($LASTEXITCODE) { throw 'app publish (framework-dependent) failed' }
    Copy-Item (Join-Path $appFd 'Max1000Forth.exe') (Join-Path $root 'app\Max1000Forth.exe')

    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    foreach ($d in 'firmware', 'docs', 'tools', 'app') { New-Item -ItemType Directory -Force (Join-Path $stage $d) | Out-Null }
    Copy-Item (Join-Path $appOut 'Max1000Forth.exe') (Join-Path $stage 'app')
    Copy-Item build\output_files\max1000_forth.pof, build\output_files\max1000_forth.sof, scripts\program_flash.ps1 `
        (Join-Path $stage 'firmware')
    Copy-Item docs\Manual.pdf, docs\Implementation-Details.pdf, docs\design.md (Join-Path $stage 'docs')
    Copy-Item -Recurse docs\images (Join-Path $stage 'docs')
    Copy-Item tools\mforth.py, tools\hf16.py (Join-Path $stage 'tools')
    Copy-Item -Recurse examples $stage
    Copy-Item README.md, CHANGELOG.md, LICENSE $stage

    $zip = Join-Path $dist "MAX1000-FORTH-$Version.zip"
    Remove-Item -Force $zip -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
    Write-Host ("RELEASE: {0} ({1:N0} bytes)" -f $zip, (Get-Item $zip).Length)
} finally {
    Pop-Location
}
