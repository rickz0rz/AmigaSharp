# Run Prevue Guide (ESQ) with the programs in this directory.
#
# Usage: .\run-prevue.ps1 --drive <directory> [options] [<launcher options>]
#    or: run-prevue.cmd --drive <directory> [options] [<launcher options>]
#
# Options:
#   --drive <directory>      The drive of the Prevue machine (its DH1:). The script copies it to its data directory on
#                            the first run, and never changes the original.
#   --esq <file>             The ESQ program. The default is ESQ on the drive.
#   --code <code>            The selection code of the machine. The default is GA24005.
#   --channels-dvr <url>     Show the listings of a Channels DVR server, for example http://channels-dvr.local:8089. The
#                            listings stay current while Prevue runs.
#   --interval <minutes>     The minutes between two reads of the guide. The default is 10.
#   --premium <list>         The premium channels of the Channels DVR listings: channel numbers or call signs, with
#                            commas between them, for example 222,HBOHD. Their programs have a red background.
#   --date <date>            The date and the time of the Amiga, for example 2020-11-01T16:00. Use it to show the saved
#                            listings of the drive on their date. The default is the time of this computer.
#   --stream <port>          Stream the display as a TV channel on the HTTP port (ffmpeg must be installed). The playlist
#                            for Channels DVR is http://<this computer>:<port>/channels.m3u.
#   --audio <path>           Music for the stream: an M3U playlist or a directory of audio files, in a loop. By
#                            default, it stops while a --genlock video with sound plays.
#   --genlock <file or URL>  Show a video behind the grid of the stream, as the genlock of the Prevue channel did. A
#                            file plays in a loop. A URL plays live, for example a channel of an HDHomeRun tuner.
#                            The stream has the sound of this video.
#   --genlock-control        Start the genlock with no video, over black. The stream then takes a queue of videos
#                            from http://<this computer>:<port>/genlock. See the README of AmigaSharp.
#   --genlock-playlist <file> Start the genlock with the videos of a JSON file, for example to play a video and
#                            then a pause of "black" in a loop. See the README of AmigaSharp.
#   --prevue-ctrl-port <port> A TCP port for the control line of Prevue. Its commands show promos of programs. With
#                            --stream, the stream port also takes them at /prevue/ctrl. See docs/ctrl-line.md.
#   --name <name>            The name of the channel of the stream. The default is "Prevue Guide".
#   --headless               Do not open a window. Stop with Ctrl-C.
#   --reset                  Delete the copy of the drive, and copy the drive again.
#
# Other options go to the launcher, for example --scale 3.
#
# Environment variables:
#   PREVUE_DATA              The data directory. The default is %LOCALAPPDATA%\AmigaSharp Prevue.
#
# This is the Windows version of run-prevue.sh, with the same options. The options for the launcher can come after a
# "--", as in run-prevue.sh, but they do not need one.
$ErrorActionPreference = 'Stop'

function Show-Usage {
    Get-Content $PSCommandPath | Select-Object -First 38 | ForEach-Object { $_ -replace '^# ?', '' }
}

function Fail([string]$Message) {
    [Console]::Error.WriteLine("error: $Message")
    exit 2
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

$Here = $PSScriptRoot
$Launcher = Join-Path $Here 'AmigaSharp.PrevueLauncher.exe'
$Listings = Join-Path $Here 'AmigaSharp.PrevueListings.exe'

$Drive = ''; $Esq = ''; $Code = 'GA24005'; $ChannelsDvr = ''; $Interval = '10'; $Premium = ''; $Date = ''
$Stream = ''; $Audio = ''; $Genlock = ''; $GenlockPlaylist = ''; $CtrlPort = ''; $Name = 'Prevue Guide'; $Headless = $false; $Reset = $false; $GenlockControl = $false
$launcherOptions = @()
for ($i = 0; $i -lt $args.Count; $i++) {
    $option = [string]$args[$i]
    $valueOptions = '--drive', '--esq', '--code', '--channels-dvr', '--interval', '--premium', '--date', '--stream',
        '--audio', '--genlock', '--genlock-playlist', '--prevue-ctrl-port', '--name'
    if ($valueOptions -contains $option) {
        if ($i + 1 -ge $args.Count) { Fail "$option needs a value." }
        $i++
        $value = [string]$args[$i]
    }
    switch ($option) {
        '--drive' { $Drive = $value }
        '--esq' { $Esq = $value }
        '--code' { $Code = $value }
        '--channels-dvr' { $ChannelsDvr = $value }
        '--interval' { $Interval = $value }
        '--premium' { $Premium = $value }
        '--date' { $Date = $value }
        '--stream' { $Stream = $value }
        '--audio' { $Audio = $value }
        '--genlock' { $Genlock = $value }
        '--genlock-playlist' { $GenlockPlaylist = $value }
        '--prevue-ctrl-port' { $CtrlPort = $value }
        '--name' { $Name = $value }
        '--headless' { $Headless = $true }
        '--genlock-control' { $GenlockControl = $true }
        '--reset' { $Reset = $true }
        { $_ -in '-h', '--help', '-?' } { Show-Usage; exit 0 }
        '--' {
            # PowerShell keeps a "--" in the arguments when run-prevue.cmd starts the script.
            if ($i + 1 -lt $args.Count) { $launcherOptions += $args[($i + 1)..($args.Count - 1)] | ForEach-Object { [string]$_ } }
            $i = $args.Count
        }
        default {
            # The launcher checks its own options, so a wrong option still stops with an error.
            $launcherOptions += $args[$i..($args.Count - 1)] | ForEach-Object { [string]$_ }
            $i = $args.Count
        }
    }
}

if (-not $Drive) { Show-Usage | Write-Host; Fail '--drive is necessary.' }
if (-not (Test-Path -PathType Container $Drive)) { Fail "the drive $Drive does not exist." }
if (-not (Test-Path $Launcher)) { Fail "$Launcher is missing. Keep the files of this directory together." }
if ($Stream -and -not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
    Fail "--stream needs ffmpeg. Install it, for example with 'winget install Gyan.FFmpeg'."
}

$PrevueData = $env:PREVUE_DATA
if (-not $PrevueData) { $PrevueData = Join-Path $env:LOCALAPPDATA 'AmigaSharp Prevue' }
$Work = Join-Path $PrevueData 'drive'

# Prevue writes to its drive, so it runs on a copy. PowerPacker packs the saved listings, and Prevue cannot read packed
# files, so the script unpacks them in the copy.
if ($Reset -and (Test-Path $Work)) {
    Remove-Item -Recurse -Force $Work
}
if (-not (Test-Path $Work)) {
    Write-Host "Copying the drive to $Work."
    New-Item -ItemType Directory -Force $PrevueData | Out-Null
    Copy-Item -Recurse $Drive $Work
    Get-ChildItem $Work | Where-Object { $_.Name -match '^(curday\.dat|nxtday\.dat|PWI.)$' } | ForEach-Object {
        Invoke-Program $Launcher unpack $_.FullName --output $Work | Out-Null
    }
}

if (-not $Esq) { $Esq = Join-Path $Work 'ESQ' }
if (-not (Test-Path -PathType Leaf $Esq)) { Fail "the program $Esq does not exist. Use --esq." }

$arguments = @($Esq, '--drive', $Work, '--volume', "DH1=$Work", '--assign', 'DF0=DH1:', '--assign', 'ENV=DH1:',
    '--arguments', $Code, '--command-name', 'esq', '--turbo', '8', '--deinterlace', 'blend', '--scale', '2')
$arguments += $launcherOptions
if ($Date) { $arguments += '--date', $Date }
if ($Headless) { $arguments += '--headless' }
if ($CtrlPort) { $arguments += '--prevue-ctrl-port', $CtrlPort }
if ($Stream) {
    $arguments += '--stream', $Stream, '--stream-name', $Name
    if ($Audio) { $arguments += '--stream-audio', $Audio }
    if ($Genlock) { $arguments += '--genlock', $Genlock }
    if ($GenlockControl) { $arguments += '--genlock-control' }
    if ($GenlockPlaylist) { $arguments += '--genlock-playlist', $GenlockPlaylist }
}

$tool = $null
try {
    if ($ChannelsDvr) {
        # The listings tool writes the listing files, makes the ready file, and then sends the changes of the guide to
        # the serial port of the launcher (TCP port 5400).
        $ready = Join-Path $PrevueData 'listings-ready'
        Remove-Item -Force $ready -ErrorAction SilentlyContinue
        $toolArguments = @('--server', (Quote $ChannelsDvr), '--output', (Quote $Work), '--ready', (Quote $ready),
            '--serve', 'localhost:5400', '--interval', $Interval)
        if ($Premium) { $toolArguments += '--premium', (Quote $Premium) }
        if ($Date) { $toolArguments += '--clock', (Quote $Date) }
        $tool = Start-Process -FilePath $Listings -ArgumentList $toolArguments -NoNewWindow -PassThru
        while (-not (Test-Path $ready)) {
            if ($tool.HasExited) { Fail "the listings tool stopped. Is $ChannelsDvr correct?" }
            Start-Sleep -Seconds 1
        }
        # ESQ parses the feed faster than 4 times 2400 baud, so a large update arrives sooner.
        $arguments += '--serial-speed', '4'
    }

    Invoke-Program $Launcher @arguments
    $exitCode = $LASTEXITCODE
}
finally {
    if ($tool -and -not $tool.HasExited) { Stop-Process -Id $tool.Id -Force -ErrorAction SilentlyContinue }
}
exit $exitCode
