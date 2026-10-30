<#
.SYNOPSIS
    Compatibility entry point for the English verification report generator.
.DESCRIPTION
    Existing commands can keep using this path. New commands should use
    Tools/Generate-VerificationSummary.ps1.
#>
[CmdletBinding()]
param(
    [string]$ConnectionString = $env:POSTGRES_TEST_CONNECTION,
    [string]$OutputPath,
    [string]$Dotnet = 'dotnet',
    [switch]$NoRestore
)
& (Join-Path $PSScriptRoot 'Generate-VerificationSummary.ps1') @PSBoundParameters
exit $LASTEXITCODE
