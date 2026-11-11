param(
    [Parameter(Mandatory = $true)][string]$GamePath,
    [string]$DotnetPath = 'dotnet',
    [switch]$RunSmokeTests
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $projectRoot 'ArcaneTravelCircle.csproj'
if (-not (Test-Path -LiteralPath (Join-Path $GamePath 'StardewModdingAPI.dll'))) {
    throw 'GamePath must be the Stardew Valley directory containing an installed SMAPI.'
}
& $DotnetPath build $project -c Release "-p:GamePath=$GamePath" '-p:EnableModDeploy=false'
if ($LASTEXITCODE -ne 0) { throw "Release build failed ($LASTEXITCODE)." }
if ($RunSmokeTests) {
    $tests = Join-Path $projectRoot 'tests\SmokeTests\SmokeTests.csproj'
    & $DotnetPath build $tests -c Release "-p:GamePath=$GamePath" '-p:EnableModDeploy=false' '-p:EnableModZip=false'
    if ($LASTEXITCODE -ne 0) { throw "Smoke-test build failed ($LASTEXITCODE)." }
    & $DotnetPath (Join-Path $projectRoot 'tests\SmokeTests\bin\Release\net6.0\SmokeTests.dll') $GamePath (Join-Path $projectRoot 'bin\Release\net6.0')
    if ($LASTEXITCODE -ne 0) { throw "Smoke tests failed ($LASTEXITCODE)." }
}
Write-Output 'Build finished. No files were installed into the game Mods directory.'
