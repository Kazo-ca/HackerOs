<#
.SYNOPSIS
    Removes the KazoOS Server service and program files (ADR 0191).
.DESCRIPTION
    Keeps the data folder (accounts, synchronized files, certificates) unless -Purge is given.
    -InstallDir and -DataDir must match the ones given to install.ps1, if any.
#>
[CmdletBinding()]
param(
    [switch] $Purge,
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'KazoOS Server'),
    [string] $DataDir = (Join-Path $env:ProgramData 'KazoOS Server')
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'HackerOSServer'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script in PowerShell started as administrator.'
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $ServiceName | Out-Null
}
Get-NetFirewallRule -DisplayName 'KazoOS Server (local network)' -ErrorAction SilentlyContinue | Remove-NetFirewallRule

# Only a folder that holds the server program is removed, never a mistyped -InstallDir.
if (Test-Path (Join-Path $InstallDir 'HackerOs.Server.exe')) {
    Remove-Item -Path $InstallDir -Recurse -Force
}

if ($Purge) {
    $caPath = Join-Path $DataDir 'certs\hackeros-local-ca.crt'
    if (Test-Path $caPath) {
        $ca = New-Object Security.Cryptography.X509Certificates.X509Certificate2 $caPath
        Remove-Item -Path "Cert:\LocalMachine\Root\$($ca.Thumbprint)" -ErrorAction SilentlyContinue
    }
    if (Test-Path $DataDir) { Remove-Item -Path $DataDir -Recurse -Force }
    Write-Host 'KazoOS Server and its data were removed.'
} else {
    Write-Host "KazoOS Server was removed; its data stays in $DataDir (-Purge deletes it)."
}
