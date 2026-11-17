#requires -Version 7.6
<#
.SYNOPSIS
Builds, tests, and stages the JAz.CloudShell module into Artifacts\publish\JAz.CloudShell\<configuration>.
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$artifacts = Join-Path $PSScriptRoot 'Artifacts'
$out = Join-Path $artifacts 'publish' 'JAz.CloudShell' $Configuration.ToLowerInvariant()
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish (Join-Path $PSScriptRoot 'src' 'JAz.CloudShell') -c $Configuration -o $out --nologo
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)." }

# SMA is provided by PowerShell itself; never ship a copy.
Get-ChildItem $out -Filter 'System.Management.Automation.dll' -Recurse | Remove-Item -Force
Copy-Item (Join-Path $PSScriptRoot 'README.md'), (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.md') $out

if (-not $SkipTests) {
    Push-Location $PSScriptRoot
    try {
        $results = Join-Path $artifacts 'TestResults' $Configuration.ToLowerInvariant()
        dotnet test --project (Join-Path 'tests' 'JAz.CloudShell.Tests') -c $Configuration --results-directory $results
        if ($LASTEXITCODE) { throw "Tests failed ($LASTEXITCODE)." }
    } finally {
        Pop-Location
    }
}

Write-Host "Module staged at $out"
