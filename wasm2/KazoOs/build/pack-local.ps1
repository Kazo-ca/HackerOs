# ADR 0098 §2: packs every library Kazo OS publishes (each project importing
# Platform/Packaging.props, plus the templates) into artifacts/local-nupkg.
# Local only: nothing is ever pushed to nuget.org. Another repository consumes the
# folder through a nuget.config (sample: build/nuget.config.sample).
#
#   build/pack-local.ps1 [-Configuration Release] [-Output <dir>]
#
# Re-packing the same version would leave NuGet's global cache serving the previous
# bytes, so the script also removes that version of each Kazo.Os.* package from it.
param(
    [string] $Configuration = "Release",
    [string] $Output = ""
)
$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
if ($Output -eq "") { $Output = Join-Path $root "artifacts/local-nupkg" }
[xml] $versions = Get-Content (Join-Path $root "build/Versions.props")
$version = $versions.Project.PropertyGroup.KazoOsVersion
New-Item -ItemType Directory -Force -Path $Output, (Join-Path $root "artifacts") | Out-Null

$projects = Get-ChildItem -Path $root -Recurse -Filter *.csproj |
    Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' } |
    Where-Object { Select-String -Path $_.FullName -Pattern 'Packaging.props' -Quiet } |
    ForEach-Object { [System.IO.Path]::GetRelativePath($root, $_.FullName) } |
    Sort-Object
$filter = Join-Path $root "artifacts/packages.slnf"
@{ solution = @{ path = "../Kazo.Os.sln"; projects = @($projects) } } | ConvertTo-Json -Depth 4 | Set-Content $filter

Write-Host "Packing $($projects.Count) projects, version $version, into $Output"
dotnet pack $filter -c $Configuration -o $Output -p:ContinuousIntegrationBuild=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME ".nuget/packages" }
Get-ChildItem -Path $Output -Filter "*.$version.nupkg" | ForEach-Object {
    $id = $_.Name.Substring(0, $_.Name.Length - ".$version.nupkg".Length).ToLowerInvariant()
    $cached = Join-Path (Join-Path $cache $id) $version
    if (Test-Path $cached) { Remove-Item -Recurse -Force $cached }
}
Write-Host "Done."
