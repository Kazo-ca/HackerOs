<#
.SYNOPSIS
Runs either browser or non-browser tests after the solution has been built.
.DESCRIPTION
Classifies actual test projects by their Playwright dependency, rather than a
test-name filter that could accidentally start a browser fixture on CI.

With -RetryFailedTests N, a failing project reruns only its failed tests, up to
N more times. A test that then passes is reported as flaky (a GitHub warning
annotation and a line in the job summary) instead of failing the suite; a test
that keeps failing, or a project that fails without a readable .trx, still fails.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('NonBrowser', 'Browser')]
    [string] $Suite,
    [string] $Configuration = 'Release',
    [ValidateRange(0, 3)]
    [int] $RetryFailedTests = 0,
    [switch] $ListOnly
)

$ErrorActionPreference = 'Stop'
$solutionRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$selectedProjects = @(
    Get-ChildItem (Join-Path $solutionRoot 'Tests') -Directory | Sort-Object Name | ForEach-Object {
        Get-ChildItem $_.FullName -Filter '*.csproj' -File | ForEach-Object {
            [xml] $project = [IO.File]::ReadAllText($_.FullName)
            $packages = @($project.SelectNodes('//PackageReference') | ForEach-Object { $_.GetAttribute('Include') })
            if ($packages -notcontains 'Microsoft.NET.Test.Sdk') { return }
            $references = @($project.SelectNodes('//ProjectReference') | ForEach-Object { $_.GetAttribute('Include') })
            $usesBrowser = $packages -contains 'Microsoft.Playwright' -or
                @($references | Where-Object { $_ -match 'HackerOs\.E2E\.Support\.csproj$' }).Count -gt 0
            if ($usesBrowser -eq ($Suite -eq 'Browser')) { $_ }
        }
    }
)
if ($selectedProjects.Count -eq 0) { throw "No test projects selected for $Suite." }

Write-Output "$Suite suite: $($selectedProjects.Count) test projects."
if ($ListOnly) {
    $selectedProjects | ForEach-Object { [IO.Path]::GetRelativePath($solutionRoot, $_.FullName) }
    return
}

function Get-FailedTestNames([string] $ResultsDirectory) {
    $trx = Get-ChildItem $ResultsDirectory -Filter '*.trx' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc | Select-Object -Last 1
    if ($null -eq $trx) { return @() }
    [xml] $results = [IO.File]::ReadAllText($trx.FullName)
    $failedIds = @($results.TestRun.Results.UnitTestResult |
        Where-Object { $_.outcome -eq 'Failed' } | ForEach-Object { $_.testId })
    # A theory's name carries its arguments; the filter matches the method.
    @($results.TestRun.TestDefinitions.UnitTest |
        Where-Object { $failedIds -contains $_.id } |
        ForEach-Object { $_.TestMethod.className + '.' + ($_.TestMethod.name -replace '\(.*', '') } |
        Sort-Object -Unique)
}

function Invoke-TestProject($TestProject, [string] $ResultsDirectory, [string] $Filter) {
    $arguments = @($TestProject.FullName, '--configuration', $Configuration, '--no-build', '--no-restore',
        '--logger', 'trx;LogFilePrefix=hackeros', '--results-directory', $ResultsDirectory)
    if ($Filter) { $arguments += @('--filter', $Filter) }
    # Out-Host keeps the test output out of this function's return value.
    & dotnet test @arguments | Out-Host
    $LASTEXITCODE
}

function Write-SummaryLine([string] $Line) {
    if ($env:GITHUB_STEP_SUMMARY) { Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $Line }
}

# Keep projects sequential: browser suites can publish the same host and share
# intermediate outputs. Record every failure so later projects still run.
$failedProjects = @()
$flakyTests = @()
Push-Location $solutionRoot
try {
    foreach ($testProject in $selectedProjects) {
        $resultsDirectory = Join-Path $solutionRoot "artifacts/test-results/$Suite/$($testProject.BaseName)"
        $exitCode = Invoke-TestProject $testProject $resultsDirectory $null
        $failedTests = @(if ($exitCode -ne 0) { Get-FailedTestNames $resultsDirectory })
        for ($attempt = 1; $exitCode -ne 0 -and $attempt -le $RetryFailedTests; $attempt++) {
            # No failed test in the .trx means a crash or a build problem: never retry it.
            if ($failedTests.Count -eq 0) { break }
            Write-Output "Retry $attempt/$RetryFailedTests of $($testProject.BaseName): $($failedTests -join ', ')"
            $filter = ($failedTests | ForEach-Object { "FullyQualifiedName=$_" }) -join '|'
            $retryDirectory = Join-Path $resultsDirectory "retry-$attempt"
            $exitCode = Invoke-TestProject $testProject $retryDirectory $filter
            $stillFailing = @(if ($exitCode -ne 0) { Get-FailedTestNames $retryDirectory })
            if ($exitCode -ne 0 -and $stillFailing.Count -eq 0) { break }
            $flakyTests += @($failedTests | Where-Object { $stillFailing -notcontains $_ } |
                ForEach-Object { "$($testProject.BaseName): $_" })
            $failedTests = $stillFailing
        }
        if ($exitCode -ne 0) { $failedProjects += $testProject.BaseName }
    }
}
finally { Pop-Location }

if ($flakyTests.Count -gt 0) {
    Write-SummaryLine "### Tests instables ($Suite) : échec puis réussite au nouvel essai"
    foreach ($flakyTest in $flakyTests) {
        Write-Output "::warning title=Flaky test::$flakyTest failed, then passed on retry."
        Write-SummaryLine "- ``$flakyTest``"
    }
}

if ($failedProjects.Count -gt 0) {
    Write-SummaryLine "### Échecs ($Suite) : $($failedProjects -join ', ')"
    throw "Test projects failed: $($failedProjects -join ', ')"
}
