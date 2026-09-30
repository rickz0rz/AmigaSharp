<#
.SYNOPSIS
Run Prevue (ESQ) in a window.

.DESCRIPTION
The first run copies the drive to build\drive\ and unpacks the saved 2020 listing files there. ESQ writes to its
drive, so the script never uses target-source\binaries directly. Delete build\drive\ to start again from the original
drive. With -ChannelsDvr, the script uses a separate copy in build\drive-channels-dvr\.

The 68000 runs as fast as the host can until the main loop of ESQ starts, and then at its real speed. Add --fast-cpu
to run as fast as the host can all the time.

Arguments that are not parameters of the script go to the launcher. It is the PowerShell version of run-esq.sh. Each
parameter has a default from the environment variable of run-esq.sh.

.PARAMETER ChannelsDvr
The address of a Channels DVR server, for example http://channels-dvr.local:8089 (CHANNELS_DVR). The script then
writes new listing files from the guide of the server before each run, and the Amiga uses the time of the host. While
ESQ runs, the listings tool sends the changes of the guide to the serial port (TCP port 5400), so the grid stays
current.

.PARAMETER Interval
The minutes between two reads of the guide (CHANNELS_DVR_INTERVAL). The default is 10.

.PARAMETER Premium
The premium channels: channel numbers or call signs, with commas between them, for example 222,HBOHD
(CHANNELS_DVR_PREMIUM). Their programs have a red background.

.PARAMETER Date
The date and the time of the Amiga at the start (ESQ_DATE). Without -ChannelsDvr, the default is 2020-11-01T16:00, the
date of the saved listings. With -ChannelsDvr, the listings tool uses it too, to choose the current and the next
broadcast day.

.PARAMETER Scale
The size of the window (ESQ_SCALE). The default is 2 (1536 by 960 pixels). The window becomes smaller if it does not
fit on the screen.

.EXAMPLE
scripts\run-esq.ps1 --scale 1

.EXAMPLE
scripts\run-esq.ps1 -ChannelsDvr http://channels-dvr.local:8089
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$ChannelsDvr = $env:CHANNELS_DVR,
    [int]$Interval = $(if ($env:CHANNELS_DVR_INTERVAL) { [int]$env:CHANNELS_DVR_INTERVAL } else { 10 }),
    [string]$Premium = $env:CHANNELS_DVR_PREMIUM,
    [string]$Date = $env:ESQ_DATE,
    [int]$Scale = $(if ($env:ESQ_SCALE) { [int]$env:ESQ_SCALE } else { 2 }),
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$LauncherArguments
)
$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    [Console]::Error.WriteLine("error: $Message")
    exit 1
}

# When the output of the script goes to a file or a pipe, Windows PowerShell makes an error of each line that a program
# writes to stderr. With Stop, the first message of the launcher would end the script, so programs run with Continue.
function Invoke-Program {
    $ErrorActionPreference = 'Continue'
    $program, $rest = $args
    if ($null -eq $rest) { $rest = @() }
    & $program @rest
}

# Start-Process joins its arguments with spaces, so an argument with a space needs quotes.
function Quote([string]$Text) {
    if ($Text -match '[\s"]') { '"' + ($Text -replace '"', '\"') + '"' } else { $Text }
}

$Root = Split-Path -Parent $PSScriptRoot
$Drive = Join-Path $Root $(if ($ChannelsDvr) { 'build\drive-channels-dvr' } else { 'build\drive' })
$Esq = Join-Path $Root 'build\target\ESQ'
$Launcher = Join-Path $Root 'AmigaSharp.Launcher\bin\Release\net10.0\AmigaSharp.Launcher.dll'
$ListingsTool = Join-Path $Root 'AmigaSharp.PrevueListings\bin\Release\net10.0\AmigaSharp.PrevueListings.dll'

if (-not (Test-Path $Esq)) {
    & (Join-Path $PSScriptRoot 'build-target.ps1')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# The script runs the programs with dotnet, not with dotnet run, so that the output of the build does not mix with the
# output of the programs.
Invoke-Program dotnet build (Join-Path $Root 'AmigaSharp.Launcher') -c Release -v quiet -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { Fail 'the launcher does not build. Run dotnet build to see the errors.' }

if (-not (Test-Path $Drive)) {
    Write-Host "Copying the drive to $Drive."
    New-Item -ItemType Directory -Force (Join-Path $Root 'build') | Out-Null
    Copy-Item -Recurse (Join-Path $Root 'target-source\binaries') $Drive
    $packed = @(Get-ChildItem $Drive | Where-Object { $_.Name -match '^(curday\.dat|nxtday\.dat|PWI.)$' } |
        ForEach-Object { $_.FullName })
    Invoke-Program dotnet $Launcher unpack @packed --output $Drive
    if ($LASTEXITCODE -ne 0) { Fail 'the listing files of the drive do not unpack.' }
}

$options = @()
$tool = $null
try {
    if ($ChannelsDvr) {
        # The listings tool writes the files, makes the ready file, and then sends the changes of the guide to the
        # serial bridge of the launcher until the script stops it.
        $ready = Join-Path $Drive '.listings-ready'
        Remove-Item -Force $ready -ErrorAction SilentlyContinue
        Invoke-Program dotnet build (Join-Path $Root 'AmigaSharp.PrevueListings') -c Release -v quiet -nologo | Out-Null
        if ($LASTEXITCODE -ne 0) { Fail 'the listings tool does not build. Run dotnet build to see the errors.' }
        $toolArguments = @((Quote $ListingsTool), '--server', (Quote $ChannelsDvr), '--output', (Quote $Drive),
            '--ready', (Quote $ready), '--serve', 'localhost:5400', '--interval', $Interval)
        if ($Premium) { $toolArguments += '--premium', (Quote $Premium) }
        if ($Date) { $toolArguments += '--clock', (Quote $Date) }
        $tool = Start-Process -FilePath dotnet -ArgumentList $toolArguments -NoNewWindow -PassThru
        while (-not (Test-Path $ready)) {
            if ($tool.HasExited) { Fail 'the listings tool stopped.' }
            Start-Sleep -Seconds 1
        }
        # A feed of changes can be large, for example the listings of the next day. At 4 times 2400 baud, ESQ parses
        # the bytes faster than they arrive.
        $options += '--serial-speed', '4'
        if ($Date) { $options += '--date', $Date }
    }
    else {
        $options += '--date', $(if ($Date) { $Date } else { '2020-11-01T16:00' })
    }

    $arguments = @($Esq, '--listing', (Join-Path $Root 'build\target\ESQ.lst'),
        '--drive', $Drive, '--volume', "DH1=$Drive",
        '--assign', 'DF0=DH1:', '--assign', 'ENV=DH1:', '--arguments', 'GA24005', '--command-name', 'esq', '--prevue',
        '--turbo-until', '_ESQ_MainLoopUiTickEnabledFlag') + $options + @('--scale', $Scale)
    if ($LauncherArguments) { $arguments += $LauncherArguments }
    Invoke-Program dotnet $Launcher @arguments
    $exitCode = $LASTEXITCODE
}
finally {
    if ($tool -and -not $tool.HasExited) { Stop-Process -Id $tool.Id -Force -ErrorAction SilentlyContinue }
}
exit $exitCode
