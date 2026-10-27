[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('start', 'stop', 'status', 'doctor', 'logs', 'run', 'build', 'publish', 'test', 'format', 'check', 'latency')]
    [string]$Command = 'status',
    [string]$Config,
    [switch]$Follow,
    [switch]$AudioSmoke,
    [switch]$Microphone
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$localSdk = Join-Path $repo '.tools\dotnet\dotnet.exe'
$dotnet = 'dotnet'
if (Test-Path -LiteralPath $localSdk) {
    $dotnet = $localSdk
    $env:DOTNET_ROOT = Split-Path $localSdk
}

$project = Join-Path $repo 'src\Raumklang.DesktopStream\Raumklang.DesktopStream.csproj'
$published = Join-Path $repo 'artifacts\publish\Raumklang.DesktopStream.exe'
$built = Join-Path $repo 'src\Raumklang.DesktopStream\bin\Release\net10.0-windows10.0.19041.0\win-x64\Raumklang.DesktopStream.exe'

try {
    if ($Command -in @('format', 'check')) {
        & (Join-Path $repo 'scripts\Style.ps1') -Dotnet $dotnet -Check:($Command -eq 'check')
        exit $LASTEXITCODE
    }

    if ($Command -eq 'logs' -and $Follow) {
        $log = Join-Path $env:LOCALAPPDATA 'Raumklang.DesktopStream\raumklang.log'
        if (-not (Test-Path -LiteralPath $log)) {
            Write-Output 'No logs yet.'
            exit 0
        }

        Get-Content -LiteralPath $log -Tail 100 -Wait
        exit 0
    }

    if ($Command -eq 'test') {
        $testArguments = @('run', '--project', (Join-Path $repo 'tests\Raumklang.DesktopStream.Tests'), '-c', 'Release')
        if ($AudioSmoke) {
            $testArguments += @('--', '--audio-smoke')
        }

        & $dotnet @testArguments
        exit $LASTEXITCODE
    }

    if ($Command -eq 'publish') {
        & $dotnet publish $project -c Release -r win-x64 --self-contained true -o (Join-Path $repo 'artifacts\publish')
        exit $LASTEXITCODE
    }

    if ($Command -eq 'build' -or (-not (Test-Path -LiteralPath $published) -and -not (Test-Path -LiteralPath $built))) {
        & $dotnet build (Join-Path $repo 'Raumklang.DesktopStream.slnx') -c Release
        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }

        if ($Command -eq 'build') {
            exit 0
        }
    }

    $exe = $built
    if (Test-Path -LiteralPath $published) {
        $exe = $published
    }

    $arguments = @($Command)
    if ($Microphone) {
        $arguments += '--microphone'
    }
    if ($Config) {
        $arguments += @('--config', (Resolve-Path -LiteralPath $Config).Path)
    } elseif (Test-Path -LiteralPath (Join-Path $repo 'raumklang.local.json')) {
        $arguments += @('--config', (Join-Path $repo 'raumklang.local.json'))
    }

    & $exe @arguments
    exit $LASTEXITCODE
} catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
