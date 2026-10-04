$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$dotnetPath = Join-Path $repositoryRoot '.tools\dotnet\dotnet.exe'
$projectPath = Join-Path $repositoryRoot 'src\WindowAgentMcp\WindowAgentMcp.csproj'
$publishDirectory = Join-Path $repositoryRoot 'artifacts\publish\win-x64'
$expectedExecutable = Join-Path $publishDirectory 'WindowAgentMcp.exe'

if (-not (Test-Path -LiteralPath $dotnetPath)) {
    throw "Local .NET SDK not found at $dotnetPath. Run eng\install-dotnet.ps1 first."
}

$env:DOTNET_CLI_HOME = Join-Path $repositoryRoot '.tools\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $repositoryRoot '.tools\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

Push-Location $repositoryRoot
try {
    & $dotnetPath publish $projectPath `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishTrimmed=false `
        -o $publishDirectory
    $publishExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

if ($publishExitCode -ne 0) {
    throw "Publish failed with exit code $publishExitCode."
}

if (-not (Test-Path -LiteralPath $expectedExecutable)) {
    throw "Publish completed without creating $expectedExecutable."
}

Write-Host "Published $expectedExecutable"
