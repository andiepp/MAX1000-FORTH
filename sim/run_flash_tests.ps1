# ISS tests for the serial flash words and the saved source (store, autostart, skips,
# errors, missing final CR). Prints PASS/FAIL per check; exit code 1 on any failure.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$img = Join-Path $env:TEMP 'mf_flash_test.bin'
$fail = 0

function Run([string[]]$lines, [switch]$Button, [switch]$Fresh) {
    if ($Fresh -and (Test-Path $img)) { Remove-Item $img }
    # input through a file: PowerShell 5.1 mangles native arguments that contain quotes
    $in = Join-Path $env:TEMP 'mf_flash_input.txt'
    [System.IO.File]::WriteAllText($in, (($lines | ForEach-Object { $_ + "`n" }) -join ''))
    $a = @('tools/hf16.py', 'run', 'forth/kernel.fs', '--flash', $img, '-f', $in)
    if ($Button) { $a += '--button' }
    (& python @a) -join "`n"
}
function Check([string]$name, [string]$out, [string]$pattern, [switch]$Not) {
    $hit = $out -match $pattern
    if ($hit -xor $Not) { "PASS  $name" } else { "FAIL  $name"; "----"; $out; "----"; $script:fail++ }
}
function Store-Lines([string[]]$src, [int]$flags) {
    $len = ($src | ForEach-Object { $_.Length + 1 } | Measure-Object -Sum).Sum
    @("$len $flags store") + $src
}

$src = Get-Content sim/saved_source.fs

$o = Run @('hex flash-id . . . decimal') -Fresh
Check 'flash-id' $o '17 40 EF'

$o = Run (Store-Lines $src 1) -Fresh
Check 'store ok' $o 'ready[\s\S]*stored \d+ bytes'

$o = Run @('runs ? hello')
Check 'autostart ran the source' $o 'hello from flash'
Check 'source not echoed' $o 'variable runs' -Not
Check 'variable from source' $o 'runs \? hello 1 hello from flash'

$o = Run @('.source')
Check '.source lists it' $o ': hello \( -- \)'

$o = Run @('0 autostart')
$o = Run @('hello')
Check 'autostart off' $o 'hello \?'

$o = Run @('load-source', 'runs ?')     # the source runs after the line that calls LOAD-SOURCE
Check 'load-source by hand' $o 'runs \? 1'

$o = Run @('1 autostart')
$o = Run @('hello') -Button
Check 'button skips autostart' $o 'autostart skipped[\s\S]*hello \?'

$o = Run @("$([char]27)hello")
Check 'ESC skips autostart' $o 'autostart skipped[\s\S]*hello \?'

$o = Run @(': foo ; cold', 'foo')
Check 'cold restores the dictionary' $o 'foo \?'

$bad = @(': a1 ." one" ;', 'nosuchword', ': a2 ." two" ;')
$o = Run (Store-Lines $bad 1) -Fresh
$o = Run @('a1 a2')
Check 'error stops the source' $o 'nosuchword \?[\s\S]*\(in the saved source\)'
Check 'words before the error exist' $o 'a1 a2 one'
Check 'words after the error do not' $o 'a2 \?'

# no CR after the last line: KEY supplies one
$text = ': hx ." no-final-cr" ; hx'
$o = Run @("$($text.Length) 1 store", $text) -Fresh     # the CR after $text is not part of len
Check 'store without final CR' $o 'stored \d+ bytes'
$o = Run @('7 .')
Check 'last line without CR runs' $o 'no-final-cr'
Check 'terminal works after it' $o '7 \. 7'

# more than 32767 bytes (lengths are unsigned) and many pages
$big = @(0..599 | ForEach-Object { ("\ filler line {0:D4} " -f $_) + ('x' * 50) }) + @(': bigw 4711 ;')
$o = Run (Store-Lines $big 1) -Fresh
Check 'store 41 KB' $o 'stored 4\d{4} bytes'
$o = Run @('bigw .')
Check 'autoload 41 KB' $o 'bigw \. 4711'

$o = Run @('erase-source', 'load-source 5 .')
Check 'erase-source' $o 'load-source 5 \. 5'
$o = Run @('.source 7 .')
Check 'nothing to list after erase' $o '\.source 7 \. 7'

if ($fail) { "$fail check(s) failed"; exit 1 } else { 'all flash checks passed' }
