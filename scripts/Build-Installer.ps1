[CmdletBinding()]
param(
    [string]$Compiler,
    [string]$Version = '1.0.0'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw 'Version must have the form 1.2.3.'
}
if (-not $Compiler) {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path $repo '.tools\inno\ISCC.exe')
    )
    $Compiler = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $Compiler -or -not (Test-Path -LiteralPath $Compiler)) {
    throw 'Install Inno Setup 6 (https://jrsoftware.org/isinfo.php), or pass -Compiler with the path to ISCC.exe.'
}

$package = Join-Path $repo ('artifacts\package\' + [guid]::NewGuid().ToString('N'))
& (Join-Path $repo 'raumfeld.ps1') publish -OutputDirectory $package
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
& $Compiler "/DAppVersion=$Version" "/DPublishDir=$package" (Join-Path $repo 'installer\raumfeld.iss')
exit $LASTEXITCODE
