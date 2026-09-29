<#
.SYNOPSIS
Download the SingleStepTests 68000 test vectors and the official opcode map.

.DESCRIPTION
The vectors go to build\cpu-tests\68000\, and the opcode map to build\cpu-tests\68000.official.json. The files are
about 200 MB. The script skips each file that is already present. It is the PowerShell version of
fetch-cpu-tests.sh.
#>
$ErrorActionPreference = 'Stop'
# Windows PowerShell draws a progress bar for each download, which makes the downloads much slower.
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$Root = Split-Path -Parent $PSScriptRoot
$Out = Join-Path $Root 'build\cpu-tests\68000'
$Base = 'https://raw.githubusercontent.com/SingleStepTests/680x0/main/68000/v1'
$Api = 'https://api.github.com/repos/SingleStepTests/680x0/contents/68000/v1'

New-Item -ItemType Directory -Force $Out | Out-Null
$names = (Invoke-RestMethod -Uri $Api) | ForEach-Object { $_.name } | Where-Object { $_ -like '*.json.gz' }
foreach ($name in $names) {
    $file = Join-Path $Out $name
    if (-not (Test-Path $file)) {
        Invoke-WebRequest -UseBasicParsing -Uri "$Base/$name" -OutFile "$file.part"
        Move-Item -Force "$file.part" $file
    }
}
$map = Join-Path $Root 'build\cpu-tests\68000.official.json'
if (-not (Test-Path $map)) {
    Invoke-WebRequest -UseBasicParsing -OutFile $map `
        -Uri 'https://raw.githubusercontent.com/SingleStepTests/680x0/main/map/68000.official.json'
}
Write-Host "$((Get-ChildItem $Out).Count) test files in build\cpu-tests\68000."
