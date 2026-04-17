param(
    [switch] $IncludeCodeQl
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$securityArtifactsRoot = Join-Path $repoRoot 'artifacts\security'

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed."
    }
}

function Write-Step {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Message
    )

    Write-Host ""
    Write-Host "==> $Message"
}

Push-Location $repoRoot
try {
    Write-Step 'Restore solution and CLI'
    Invoke-DotNet @('restore', 'TypeWhisper.slnx')
    Invoke-DotNet @('restore', 'src/TypeWhisper.Cli/TypeWhisper.Cli.csproj')

    Write-Step 'Restore plugin projects'
    foreach ($project in Get-ChildItem 'plugins' -Recurse -Filter '*.csproj' | Sort-Object FullName) {
        Invoke-DotNet @('restore', $project.FullName)
    }

    Write-Step 'Build repository with local analyzers enabled'
    Invoke-DotNet @('build', 'TypeWhisper.slnx', '-c', 'Release', '--no-restore', '--no-incremental')
    Invoke-DotNet @('build', 'src/TypeWhisper.Cli/TypeWhisper.Cli.csproj', '-c', 'Release', '--no-restore', '--no-incremental')

    foreach ($project in Get-ChildItem 'plugins' -Recurse -Filter '*.csproj' | Sort-Object FullName) {
        Invoke-DotNet @('build', $project.FullName, '-c', 'Release', '--no-restore', '--no-incremental')
    }

    Write-Step 'Run targeted test projects'
    Invoke-DotNet @('test', 'tests/TypeWhisper.Core.Tests/TypeWhisper.Core.Tests.csproj', '-c', 'Release', '--no-build')
    Invoke-DotNet @('test', 'tests/TypeWhisper.PluginSystem.Tests/TypeWhisper.PluginSystem.Tests.csproj', '-c', 'Release', '--no-build')

    Write-Step 'Audit NuGet dependencies'
    New-Item -ItemType Directory -Force -Path $securityArtifactsRoot | Out-Null
    $foundVulnerabilities = $false

    foreach ($project in Get-ChildItem 'src', 'tests', 'plugins' -Recurse -Filter '*.csproj' | Sort-Object FullName) {
        $safeName = ($project.FullName.Replace($repoRoot, '').TrimStart('\') -replace '[\\/:*?"<>| ]', '_')
        $reportPath = Join-Path $securityArtifactsRoot ($safeName + '.json')
        $reportJson = & dotnet list $project.FullName package --vulnerable --include-transitive --format json

        if ($LASTEXITCODE -ne 0) {
            throw "dotnet list package failed: $($project.FullName)"
        }

        $reportJson | Set-Content $reportPath
        $report = $reportJson | ConvertFrom-Json

        foreach ($projectReport in @($report.projects | Where-Object { $null -ne $_ })) {
            foreach ($framework in @($projectReport.frameworks | Where-Object { $null -ne $_ })) {
                $topLevelPackages = @($framework.topLevelPackages | Where-Object { $null -ne $_ })
                $transitivePackages = @($framework.transitivePackages | Where-Object { $null -ne $_ })

                if ($topLevelPackages.Count -gt 0 -or $transitivePackages.Count -gt 0) {
                    Write-Warning "Vulnerable packages detected in $($project.FullName)"
                    $foundVulnerabilities = $true
                }
            }
        }
    }

    if ($foundVulnerabilities) {
        throw 'One or more .NET projects have vulnerable packages.'
    }

    if ($IncludeCodeQl) {
        Write-Step 'Run local CodeQL analysis'
        $codeQlCommand = Get-Command 'codeql' -ErrorAction SilentlyContinue
        if ($null -eq $codeQlCommand) {
            throw 'CodeQL CLI was not found on PATH. Install the CodeQL bundle or rerun without -IncludeCodeQl.'
        }

        $codeQlRoot = Join-Path $repoRoot 'artifacts\codeql'
        $databasePath = Join-Path $codeQlRoot 'csharp-db'
        $sarifPath = Join-Path $codeQlRoot 'results.sarif'

        if (Test-Path $databasePath) {
            Remove-Item $databasePath -Recurse -Force
        }

        New-Item -ItemType Directory -Force -Path $codeQlRoot | Out-Null

        & codeql database create $databasePath --language=csharp --source-root=$repoRoot --command 'powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Build-CodeQlTargets.ps1'
        if ($LASTEXITCODE -ne 0) {
            throw 'CodeQL database creation failed.'
        }

        & codeql database analyze $databasePath 'codeql/csharp-queries:codeql-suites/csharp-security-and-quality.qls' --download --format=sarif-latest --output=$sarifPath
        if ($LASTEXITCODE -ne 0) {
            throw 'CodeQL analysis failed.'
        }
    }
}
finally {
    Pop-Location
}
