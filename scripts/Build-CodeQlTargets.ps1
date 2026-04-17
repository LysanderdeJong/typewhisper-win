$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

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

Push-Location $repoRoot
try {
    Invoke-DotNet @('build', 'TypeWhisper.slnx', '-c', 'Release', '--no-incremental')
    Invoke-DotNet @('build', 'src/TypeWhisper.Cli/TypeWhisper.Cli.csproj', '-c', 'Release', '--no-incremental')

    foreach ($project in Get-ChildItem 'plugins' -Recurse -Filter '*.csproj' | Sort-Object FullName) {
        Invoke-DotNet @('build', $project.FullName, '-c', 'Release', '--no-incremental')
    }
}
finally {
    Pop-Location
}
