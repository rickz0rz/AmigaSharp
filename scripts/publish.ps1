<#
.SYNOPSIS
Build the launcher for Prevue and the listings tool as native programs (Native AOT). The people who use them do not
need .NET. The launcher for Prevue also runs other AmigaOS programs.

.DESCRIPTION
Native AOT compiles only for the operating system of the host, so on Windows, build for win-x64 or win-arm64. It needs
the C++ build tools of Visual Studio (the workload "Desktop development with C++").

The script writes the programs to dist\<runtime identifier>\, with run-prevue.ps1, run-prevue.cmd and README.txt for
the users. The launcher needs the SDL2 library next to it, so keep the files of the directory together. It is the
PowerShell version of publish.sh.

.PARAMETER RuntimeIdentifier
The runtime identifier. The default is the one of this host, for example win-x64.

.PARAMETER EmbeddedProgram
An executable to translate and compile into the launcher, for example build\target\ESQ (EMBEDDED_PROGRAM). The launcher
then runs this program from its translation, and other programs in the interpreter. The launcher contains the code of
the program, so give it only to people who can have it.

.PARAMETER EmbeddedListing
The vasm listing of the embedded program (EMBEDDED_LISTING). It is optional, but it gives a better translation.
#>
param(
    [string]$RuntimeIdentifier,
    [string]$EmbeddedProgram = $env:EMBEDDED_PROGRAM,
    [string]$EmbeddedListing = $env:EMBEDDED_LISTING
)
$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    [Console]::Error.WriteLine("error: $Message")
    exit 1
}

# When the output of the script goes to a file or a pipe, Windows PowerShell makes an error of each line that a program
# writes to stderr. With Stop, the first message of the build would end the script, so programs run with Continue.
function Invoke-Program {
    $ErrorActionPreference = 'Continue'
    $program, $rest = $args
    if ($null -eq $rest) { $rest = @() }
    & $program @rest
}

$Root = Split-Path -Parent $PSScriptRoot
if (-not $RuntimeIdentifier) {
    $line = & dotnet --info | Where-Object { $_ -match '^\s*RID:' } | Select-Object -First 1
    $RuntimeIdentifier = ($line -split ':', 2)[1].Trim()
}
$Out = Join-Path $Root "dist\$RuntimeIdentifier"

$embedded = @()
if ($EmbeddedProgram) {
    $embedded += "-p:EmbeddedProgram=$((Resolve-Path $EmbeddedProgram).Path)"
    if ($EmbeddedListing) {
        $embedded += "-p:EmbeddedListing=$((Resolve-Path $EmbeddedListing).Path)"
    }
    Write-Host "The launcher gets the translation of $EmbeddedProgram."
}

if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
foreach ($project in 'AmigaSharp.PrevueLauncher', 'AmigaSharp.PrevueListings') {
    Write-Host "Publishing $project for $RuntimeIdentifier."
    # The options are strings in an array. PowerShell would split -p:Name=value in two if it passed them to a function
    # as they are.
    $publishArguments = @('publish', (Join-Path $Root $project), '-c', 'Release', '-r', $RuntimeIdentifier, '-o', $Out,
        '-nologo', '-v', 'quiet', '-p:PublishAot=true', '-p:DebugType=None', '-p:StripSymbols=true') + $embedded
    Invoke-Program dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { Fail "$project does not publish." }
}

# The build writes the files of the configuration of the translator, which the native launcher does not use.
Get-ChildItem $Out -Include '*.runtimeconfig.json', '*.pdb' -Recurse | Remove-Item -Force
Get-ChildItem $Out -Filter '*.dSYM' -Directory | Remove-Item -Recurse -Force

# The script and the instructions for the people who use the programs.
$dist = Join-Path $PSScriptRoot 'dist'
if ($RuntimeIdentifier -like 'win-*') {
    Copy-Item (Join-Path $dist 'run-prevue.ps1'), (Join-Path $dist 'run-prevue.cmd') $Out
}
else {
    Copy-Item (Join-Path $dist 'run-prevue.sh') $Out
}
Copy-Item (Join-Path $dist 'README.txt') $Out
Write-Host "The programs are in ${Out}:"
Get-ChildItem $Out | Format-Table -AutoSize Name, Length | Out-String | Write-Host
