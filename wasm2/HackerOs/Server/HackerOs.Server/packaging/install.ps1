<#
.SYNOPSIS
    Installs HackerOS Server as a Windows service (ADR 0191).

.DESCRIPTION
    Copies the server into Program Files, writes hackeros-server.json, registers the
    "HackerOS Server" service (virtual account NT AUTHORITY\LocalService), starts it and prints a
    one-time pairing code. By default the server only answers on this computer
    (http://localhost:5888). -Lan also opens it to the local network in HTTPS, with a local
    certificate authority to approve on each other device.
    Guide: https://github.com/Kazo-ca/HackerOs/blob/main/doc/en/wasm/dev/installable-server.md

.EXAMPLE
    .\install.ps1
.EXAMPLE
    .\install.ps1 -Lan 192.168.1.20,hackeros-pc.local
#>
[CmdletBinding()]
param(
    # IP addresses or host names other devices use to reach this computer.
    [string[]] $Lan = @(),
    [int] $Port = 5888,
    [int] $HttpsPort = 5889,
    # Server account the first pairing code is for.
    [ValidatePattern('^[A-Za-z0-9._-]{1,64}$')]
    [string] $Account = 'admin',
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'HackerOS Server'),
    [string] $DataDir = (Join-Path $env:ProgramData 'HackerOS Server'),
    # Rewrite hackeros-server.json even if it exists (an upgrade keeps it otherwise).
    [switch] $Reconfigure
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'HackerOSServer'
$PagesOrigin = 'https://kazo-ca.github.io'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script in PowerShell started as administrator.'
}

$source = $PSScriptRoot
if (-not (Test-Path (Join-Path $source 'HackerOs.Server.exe'))) {
    throw 'HackerOs.Server.exe was not found next to install.ps1.'
}

# ── Files ─────────────────────────────────────────────────────────────────────
Write-Host "Installing HackerOS Server into $InstallDir (data: $DataDir)"
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Stop-Service -Name $ServiceName -Force
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
foreach ($folder in 'blobs', 'backups', 'pairing', 'logs', 'certs') {
    New-Item -ItemType Directory -Force -Path (Join-Path $DataDir $folder) | Out-Null
}

$sameFolder = (Resolve-Path $source).Path.TrimEnd('\') -ieq (Resolve-Path $InstallDir).Path.TrimEnd('\')
if (-not $sameFolder) {
    # The program files are replaced; hackeros-server.json (the configuration) is kept.
    Get-ChildItem -Path $InstallDir -Force | Where-Object Name -ne 'hackeros-server.json' | Remove-Item -Recurse -Force
    Get-ChildItem -Path $source -Force | Where-Object Name -ne 'hackeros-server.json' |
        Copy-Item -Destination $InstallDir -Recurse -Force
}

# Unsigned binaries (ADR 0191 §4): remove the "downloaded from the internet" mark.
Get-ChildItem -Path $InstallDir -Recurse -File | Unblock-File

# The service account writes the data folder (inherited by every file created later).
& icacls.exe $DataDir /grant '*S-1-5-19:(OI)(CI)M' /T /Q | Out-Null

# ── Local network certificate (ADR 0191 §2, step 2) ──────────────────────────
$hosts = @('localhost', '127.0.0.1', '[::1]')
$caPath = Join-Path $DataDir 'certs\hackeros-local-ca.crt'
$pfxPath = Join-Path $DataDir 'certs\server.pfx'
$pfxPassword = $null
if ($Lan.Count -gt 0) {
    foreach ($name in $Lan) {
        if ($name -notmatch '^[A-Za-z0-9.:-]+$') { throw "Invalid -Lan name '$name'." }
        if ($name.Contains(':')) { $hosts += "[$name]" } else { $hosts += $name }
    }

    Write-Host 'Generating the local certificate authority and the HTTPS certificate'
    $ca = New-SelfSignedCertificate -Subject "CN=HackerOS Local CA ($env:COMPUTERNAME)" `
        -KeyUsage CertSign, CRLSign -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddYears(10) -CertStoreLocation 'Cert:\LocalMachine\My' `
        -TextExtension @('2.5.29.19={critical}{text}ca=true&pathlength=0')

    $san = @('DNS=localhost', 'IPAddress=127.0.0.1', 'IPAddress=::1')
    foreach ($name in $Lan) {
        $parsed = $null
        if ([Net.IPAddress]::TryParse($name, [ref] $parsed)) { $san += "IPAddress=$name" } else { $san += "DNS=$name" }
    }
    # 825 days: the longest validity Apple platforms accept for a server certificate.
    $server = New-SelfSignedCertificate -Subject "CN=$($Lan[0])" -Signer $ca `
        -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -KeyExportPolicy Exportable `
        -NotAfter (Get-Date).AddDays(825) -CertStoreLocation 'Cert:\LocalMachine\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.1', "2.5.29.17={text}$($san -join '&')")

    $caPem = "-----BEGIN CERTIFICATE-----`n" +
        [Convert]::ToBase64String($ca.RawData, 'InsertLineBreaks') + "`n-----END CERTIFICATE-----`n"
    Set-Content -Path $caPath -Value $caPem -Encoding ascii -NoNewline

    # RandomNumberGenerator.Create(): Windows PowerShell 5.1 has no static GetBytes.
    $passwordBytes = New-Object byte[] 24
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($passwordBytes)
    $pfxPassword = [Convert]::ToBase64String($passwordBytes)
    $secure = ConvertTo-SecureString -String $pfxPassword -AsPlainText -Force
    Export-PfxCertificate -Cert $server -FilePath $pfxPath -Password $secure | Out-Null

    # The authority signs this one certificate only: it leaves the store with its private key, so
    # nothing else can ever be signed with an authority the devices trust. This computer trusts it.
    Remove-Item -Path "Cert:\LocalMachine\My\$($server.Thumbprint)" -DeleteKey
    Remove-Item -Path "Cert:\LocalMachine\My\$($ca.Thumbprint)" -DeleteKey
    Import-Certificate -FilePath $caPath -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null

    # Private profile only: the server stays off public networks (cafés, airports).
    Get-NetFirewallRule -DisplayName 'HackerOS Server (local network)' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName 'HackerOS Server (local network)' -Direction Inbound -Protocol TCP `
        -LocalPort $HttpsPort -Action Allow -Profile Private | Out-Null
    $Reconfigure = $true
}

# ── Configuration (hackeros-server.json, read after appsettings.json) ────────
$configPath = Join-Path $InstallDir 'hackeros-server.json'
if (-not (Test-Path $configPath) -or $Reconfigure) {
    $endpoints = [ordered]@{ Local = @{ Url = "http://localhost:$Port" } }
    $origins = @($PagesOrigin)
    $hackerOs = [ordered]@{ Pairing = @{ Root = (Join-Path $DataDir 'pairing') } }
    if ($Lan.Count -gt 0) {
        $endpoints.Lan = @{ Url = "https://*:$HttpsPort"; Certificate = @{ Path = $pfxPath; Password = $pfxPassword } }
        foreach ($name in $Lan) {
            if ($name.Contains(':')) { $origins += "https://[$name]:$HttpsPort" } else { $origins += "https://${name}:$HttpsPort" }
        }
        $hackerOs.LocalServer = @{ CaCertificatePath = $caPath }
    }

    $config = [ordered]@{
        AllowedHosts      = $hosts -join ';'
        Kestrel           = @{ Endpoints = $endpoints }
        Cors              = @{ AllowedOrigins = $origins }
        ConnectionStrings = @{ HackerOsDb = "Data Source=$(Join-Path $DataDir 'hackeros.db')" }
        BlobStorage       = @{ Root = (Join-Path $DataDir 'blobs') }
        ServerBackup      = @{ Root = (Join-Path $DataDir 'backups') }
        HackerOs          = $hackerOs
    }
    $config | ConvertTo-Json -Depth 6 | Set-Content -Path $configPath -Encoding utf8
    # The file holds the certificate password: administrators and the service only.
    & icacls.exe $configPath /inheritance:r /grant:r '*S-1-5-32-544:F' '*S-1-5-18:F' '*S-1-5-19:R' /Q | Out-Null
    Write-Host "Wrote $configPath"
} else {
    Write-Host "Kept the existing $configPath (-Reconfigure rewrites it)"
}

# ── First pairing code (ADR 0191 §6) ─────────────────────────────────────────
Push-Location $InstallDir
try {
    $pairOutput = & (Join-Path $InstallDir 'HackerOs.Server.exe') pair $Account 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the pairing code.' }
} finally {
    Pop-Location
}

# ── Service ──────────────────────────────────────────────────────────────────
$binary = '"' + (Join-Path $InstallDir 'HackerOs.Server.exe') + '"'
if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
    New-Service -Name $ServiceName -BinaryPathName $binary -DisplayName 'HackerOS Server' `
        -Description 'Network relay, accounts and synchronization for HackerOS (ADR 0191).' -StartupType Automatic | Out-Null
}
& sc.exe config $ServiceName obj= 'NT AUTHORITY\LocalService' binPath= $binary | Out-Null
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null
Start-Service -Name $ServiceName

# ── Summary ──────────────────────────────────────────────────────────────────
Write-Host ''
Write-Host 'HackerOS Server is installed.'
Write-Host "  On this computer:   http://localhost:$Port"
foreach ($name in $Lan) {
    if ($name.Contains(':')) { $display = "[$name]" } else { $display = $name }
    Write-Host "  Local network:      https://${display}:$HttpsPort"
}
if ($Lan.Count -gt 0) {
    Write-Host "  Certificate to approve on each other device: $caPath"
    Write-Host "  (also at http://localhost:$Port/api/hackeros/local-ca.crt)"
}
Write-Host ''
$pairOutput | ForEach-Object { Write-Host $_ }
Write-Host "Another code later (PowerShell as administrator):  cd '$InstallDir'; .\HackerOs.Server.exe pair $Account"
