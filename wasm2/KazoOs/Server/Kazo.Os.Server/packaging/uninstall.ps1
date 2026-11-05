<#
.SYNOPSIS
    Removes the Kazo OS Server service and program files (ADR 0191).
.DESCRIPTION
    Keeps the data folder (accounts, synchronized files, certificates) unless -Purge is given.
    -InstallDir and -DataDir must match the ones given to install.ps1, if any.
#>
[CmdletBinding()]
param(
    [switch] $Purge,
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'Kazo OS Server'),
    [string] $DataDir = (Join-Path $env:ProgramData 'Kazo OS Server')
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'KazoOSServer'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script in PowerShell started as administrator.'
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $ServiceName | Out-Null
}
Get-NetFirewallRule -DisplayName 'Kazo OS Server (local network)' -ErrorAction SilentlyContinue | Remove-NetFirewallRule

# Only a folder that holds the server program is removed, never a mistyped -InstallDir.
if (Test-Path (Join-Path $InstallDir 'Kazo.Os.Server.exe')) {
    Remove-Item -Path $InstallDir -Recurse -Force
}

if ($Purge) {
    $caPath = Join-Path $DataDir 'certs\kazoos-local-ca.crt'
    if (Test-Path $caPath) {
        $ca = New-Object Security.Cryptography.X509Certificates.X509Certificate2 $caPath
        Remove-Item -Path "Cert:\LocalMachine\Root\$($ca.Thumbprint)" -ErrorAction SilentlyContinue
    }
    if (Test-Path $DataDir) { Remove-Item -Path $DataDir -Recurse -Force }
    Write-Host 'Kazo OS Server and its data were removed.'
} else {
    Write-Host "Kazo OS Server was removed; its data stays in $DataDir (-Purge deletes it)."
}
