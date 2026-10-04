param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$dotnetPath = Join-Path $repositoryRoot '.tools\dotnet\dotnet.exe'
$projectPath = Join-Path $repositoryRoot 'src\WindowAgentMcp\WindowAgentMcp.csproj'

if (-not (Test-Path -LiteralPath $dotnetPath)) {
    throw "Local .NET SDK not found at $dotnetPath. Run eng\install-dotnet.ps1 first."
}

$env:DOTNET_CLI_HOME = Join-Path $repositoryRoot '.tools\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $repositoryRoot '.tools\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

Push-Location $repositoryRoot
try {
    & $dotnetPath build $projectPath --configuration $Configuration
    $buildExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

if ($buildExitCode -ne 0) {
    throw "Build failed with exit code $buildExitCode."
}
