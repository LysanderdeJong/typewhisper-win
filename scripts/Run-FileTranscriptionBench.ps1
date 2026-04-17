param(
    [string]$BaseRef = "origin/main",
    [string]$BaseWorktree = "..\typewhisper-win-main-bench",
    [string]$ResultsPath = "experiments/results/file-transcription-memory.tsv",
    [int]$ShortSeconds = 180,
    [int]$LongSeconds = 900,
    [int]$ShortIterations = 5,
    [int]$LongIterations = 3,
    [int]$ChunkSeconds = 60
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path ".").Path
$benchProject = Join-Path $repoRoot "experiments/TypeWhisper.Benchmarks/TypeWhisper.Benchmarks.csproj"
$benchDll = Join-Path $repoRoot "experiments/TypeWhisper.Benchmarks/bin/Release/net10.0/TypeWhisper.Benchmarks.dll"
$currentDll = Join-Path $repoRoot "src/TypeWhisper.Windows/bin/Release/net10.0-windows/TypeWhisper.dll"
$baseRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $BaseWorktree))
$baseDll = Join-Path $baseRoot "src/TypeWhisper.Windows/bin/Release/net10.0-windows/TypeWhisper.dll"
$resultsFullPath = Join-Path $repoRoot $ResultsPath
$resultsDir = Split-Path -Parent $resultsFullPath
$tempDir = Join-Path $env:TEMP "typewhisper-file-transcription-bench"
$shortFile = Join-Path $tempDir "synthetic-${ShortSeconds}s.wav"
$longFile = Join-Path $tempDir "synthetic-${LongSeconds}s.wav"

if (-not (Test-Path $baseRoot)) {
    git worktree add --detach "$baseRoot" "$BaseRef"
}

dotnet build "$benchProject" -c Release
dotnet build "src/TypeWhisper.Windows/TypeWhisper.Windows.csproj" -c Release
dotnet build (Join-Path $baseRoot "src/TypeWhisper.Windows/TypeWhisper.Windows.csproj") -c Release

New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null
New-Item -ItemType Directory -Force -Path $tempDir | Out-Null

if (-not (Test-Path $resultsFullPath)) {
    "timestamp`tbranch`tcommit`tscenario`tinput_seconds`titerations`tstatus`tmean_ms`tmin_ms`tmax_ms`tmean_alloc_mb`tmean_peak_private_delta_mb`tmean_live_managed_delta_mb`ttotal_samples`tmean_checksum" | Out-File -FilePath $resultsFullPath -Encoding ascii
}

if (-not (Test-Path $shortFile)) {
    dotnet "$benchDll" generate-wav "$shortFile" "$ShortSeconds"
}

if (-not (Test-Path $longFile)) {
    dotnet "$benchDll" generate-wav "$longFile" "$LongSeconds"
}

$currentCommit = (git rev-parse --short HEAD).Trim()
$baseCommit = (git -C "$baseRoot" rev-parse --short HEAD).Trim()

function Add-ResultRow {
    param(
        [string]$Branch,
        [string]$Commit,
        [string]$Scenario,
        [string]$InputFile,
        [int]$Iterations
    )

    try {
        $targetDll = if ($Branch -eq "main") { $baseDll } else { $currentDll }
        $json = & dotnet "$benchDll" measure "$targetDll" "$Scenario" "$InputFile" "$Iterations" "$ChunkSeconds"
        $result = $json | ConvertFrom-Json
        $line = @(
            (Get-Date).ToString("s"),
            $Branch,
            $Commit,
            $result.scenario,
            [string]$result.inputSeconds,
            [string]$result.iterations,
            $result.status,
            [string]$result.meanMs,
            [string]$result.minMs,
            [string]$result.maxMs,
            [string]$result.meanAllocatedMb,
            [string]$result.meanPeakPrivateDeltaMb,
            [string]$result.meanLiveManagedDeltaMb,
            [string]$result.totalSamples,
            [string]$result.meanChecksum
        ) -join "`t"
        Add-Content -Path $resultsFullPath -Value $line -Encoding ascii
        $line
    }
    catch {
        $seconds = if ($InputFile -like "*${LongSeconds}s.wav") { $LongSeconds } else { $ShortSeconds }
        $line = @(
            (Get-Date).ToString("s"),
            $Branch,
            $Commit,
            $Scenario,
            [string]$seconds,
            [string]$Iterations,
            "crash",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            ""
        ) -join "`t"
        Add-Content -Path $resultsFullPath -Value $line -Encoding ascii
        Write-Warning $_
        $line
    }
}

Add-ResultRow -Branch "main" -Commit $baseCommit -Scenario "load-process" -InputFile $shortFile -Iterations $ShortIterations
Add-ResultRow -Branch "branch" -Commit $currentCommit -Scenario "load-process" -InputFile $shortFile -Iterations $ShortIterations
Add-ResultRow -Branch "main" -Commit $baseCommit -Scenario "load-process" -InputFile $longFile -Iterations $LongIterations
Add-ResultRow -Branch "branch" -Commit $currentCommit -Scenario "load-process" -InputFile $longFile -Iterations $LongIterations
Add-ResultRow -Branch "main" -Commit $baseCommit -Scenario "stream-process" -InputFile $longFile -Iterations $LongIterations
Add-ResultRow -Branch "branch" -Commit $currentCommit -Scenario "stream-process" -InputFile $longFile -Iterations $LongIterations
