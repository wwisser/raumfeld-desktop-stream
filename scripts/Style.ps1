[CmdletBinding()]
param(
    [string]$Dotnet = 'dotnet',
    [switch]$Check
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$moduleRoot = Join-Path $repo '.tools\powershell'
$modulePath = Join-Path $moduleRoot 'PSScriptAnalyzer\1.24.0\PSScriptAnalyzer.psd1'
if (-not (Test-Path -LiteralPath $modulePath)) {
    Save-Module -Name PSScriptAnalyzer -RequiredVersion 1.24.0 -Path $moduleRoot -Repository PSGallery -Force
}

Import-Module $modulePath
$settings = Join-Path $repo 'PSScriptAnalyzerSettings.psd1'
$formatArguments = @('format', (Join-Path $repo 'Raumklang.DesktopStream.slnx'), '--severity', 'warn')
if ($Check) {
    $formatArguments += '--verify-no-changes'
}

& $Dotnet @formatArguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$failed = $false
$files = @(Get-ChildItem -LiteralPath $repo -File -Filter '*.ps1') +
@(Get-ChildItem -LiteralPath (Join-Path $repo 'scripts') -File -Filter '*.ps1') +
@(Get-Item -LiteralPath $settings)
foreach ($file in $files) {
    $content = [IO.File]::ReadAllText($file.FullName)
    $formatted = (Invoke-Formatter -ScriptDefinition $content -Settings $settings).Replace("`r`n", "`n").TrimEnd() + "`n"
    if ($Check) {
        if ($content -cne $formatted) {
            Write-Output "Formatting required: $($file.FullName)"
            $failed = $true
        }
    } else {
        [IO.File]::WriteAllText($file.FullName, $formatted, [Text.UTF8Encoding]::new($false))
    }

    $diagnostics = @(Invoke-ScriptAnalyzer -Path $file.FullName -Settings $settings)
    if ($diagnostics.Count -gt 0) {
        $diagnostics | Format-Table RuleName, Severity, Line, Message -AutoSize | Out-String | Write-Output
        $failed = $true
    }
}

if ($failed) {
    exit 1
}

Write-Output 'C# and PowerShell style checks passed.'
exit 0
