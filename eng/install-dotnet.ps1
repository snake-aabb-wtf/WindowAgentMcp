$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$installDirectory = Join-Path $repositoryRoot '.tools\dotnet'
$installerPath = Join-Path $repositoryRoot '.tools\dotnet-install.ps1'

New-Item -ItemType Directory -Path (Split-Path -Parent $installerPath) -Force | Out-Null

if (-not (Test-Path -LiteralPath $installerPath) -or (Get-Item -LiteralPath $installerPath).Length -eq 0) {
    Write-Host 'Downloading the official .NET SDK installer...'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installerPath
}
else {
    Write-Host 'Using the cached official .NET SDK installer.'
}

Write-Host "Installing the .NET 10 SDK into $installDirectory ..."
$dotnetPath = Join-Path $installDirectory 'dotnet.exe'
Push-Location $repositoryRoot
try {
    & $installerPath -Channel '10.0' -InstallDir $installDirectory -NoPath
    $installedSdks = Get-ChildItem -LiteralPath (Join-Path $installDirectory 'sdk') -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like '10.0.*' }
    if (-not $installedSdks) {
        throw ".NET SDK installation did not create an SDK under $installDirectory."
    }

    $sdkVersion = & $dotnetPath --version
    $sdkExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

if ($sdkExitCode -ne 0 -or $sdkVersion -notlike '10.0.*') {
    throw "The local .NET SDK could not be selected by global.json. Reported version: $sdkVersion"
}

Write-Host "Installed SDK: $sdkVersion"
