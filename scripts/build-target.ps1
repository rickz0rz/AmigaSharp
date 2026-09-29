<#
.SYNOPSIS
Assemble the target program with vasm and check the result against the reference hash.

.DESCRIPTION
The script writes the executable and the vasm listing to build\target\. It is the PowerShell version of
build-target.sh.

.PARAMETER Vasm
The vasm executable. The default is the VASM environment variable, or vasmm68k_mot from the PATH.

.PARAMETER TargetSource
The directory that contains asm\Prevue.asm. The default is the TARGET_SOURCE environment variable, or
.\target-source.
#>
param(
    [string]$Vasm = $(if ($env:VASM) { $env:VASM } else { 'vasmm68k_mot' }),
    [string]$TargetSource = $env:TARGET_SOURCE
)
$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    [Console]::Error.WriteLine("error: $Message")
    exit 1
}

$Root = Split-Path -Parent $PSScriptRoot
if (-not $TargetSource) { $TargetSource = Join-Path $Root 'target-source' }
$Out = Join-Path $Root 'build\target'
$Expected = '6bd4760d1cf0706297ef169461ed0d7b7f0b079110a78e34d89223499e7c2fa2'

$command = Get-Command $Vasm -ErrorAction SilentlyContinue
if (-not $command) {
    Fail 'cannot find vasm. Use -Vasm or set VASM to the path of vasmm68k_mot.'
}
if (-not (Test-Path (Join-Path $TargetSource 'asm\Prevue.asm'))) {
    Fail "cannot find $TargetSource\asm\Prevue.asm. Use -TargetSource or set TARGET_SOURCE."
}

New-Item -ItemType Directory -Force $Out | Out-Null
$executable = Join-Path $Out 'ESQ'
$log = Join-Path $Out 'vasm.log'
$errors = Join-Path $Out 'vasm.err'
# Keep the default vasm optimizations. The reference binary uses them. Start-Process keeps the messages of vasm out of
# the error stream of PowerShell, and writes them to the log.
$process = Start-Process -FilePath $command.Source -WorkingDirectory (Join-Path $TargetSource 'asm') -NoNewWindow `
    -Wait -PassThru -RedirectStandardOutput $log -RedirectStandardError $errors -ArgumentList @(
        '-Fhunkexe', '-nosym', '-quiet', '-L', "`"$Out\ESQ.lst`"", '-o', "`"$executable`"", 'Prevue.asm')
Get-Content $errors | Add-Content $log
Remove-Item $errors
if ($process.ExitCode -ne 0) {
    Get-Content $log | Write-Host
    exit 1
}

$actual = (Get-FileHash -Algorithm SHA256 $executable).Hash.ToLowerInvariant()
if ($actual -ne $Expected) {
    Fail "the SHA-256 of build\target\ESQ is $actual. The reference is $Expected."
}
Write-Host 'build\target\ESQ matches the reference hash.'
exit 0
