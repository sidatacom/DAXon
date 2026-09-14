<#
.SYNOPSIS
Downloads the W3C test corpora this runner needs, at the revisions pinned in corpus.pin.

.DESCRIPTION
The corpora are not part of this repository: they are large, they belong to the W3C, and
vendoring them would pin thousands of third-party files into our history. This downloads
them as source tarballs next to the runner.

Revisions come from corpus.pin. The failure baseline in BASELINE-FAILS.txt was measured
against exactly those revisions, so a run over anything else may differ for reasons that
have nothing to do with the engine.

.PARAMETER Latest
Take each corpus at its current upstream HEAD instead of the pinned revision. Useful to
see whether new upstream tests change the picture; expect the baseline to need review.

.PARAMETER Force
Re-download even when the corpus already sits at the requested revision.

.EXAMPLE
./fetch-corpus.ps1
.EXAMPLE
./fetch-corpus.ps1 -Latest
#>
[CmdletBinding()]
param(
    [switch]$Latest,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Get-PinnedRevisions {
    $pinFile = Join-Path $root 'corpus.pin'
    if (-not (Test-Path $pinFile)) { throw "corpus.pin not found next to this script" }
    $pins = @{}
    foreach ($line in Get-Content $pinFile) {
        $trimmed = $line.Trim()
        if ($trimmed -eq '' -or $trimmed.StartsWith('#')) { continue }
        $name, $sha = $trimmed -split '=', 2
        $pins[$name.Trim()] = $sha.Trim()
    }
    return $pins
}

function Get-CorpusSize([string]$path) {
    if (-not (Test-Path $path)) { return 0 }
    return [math]::Round((Get-ChildItem $path -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
}

function Get-UpstreamHead([string]$repo) {
    $url = "https://api.github.com/repos/w3c/$repo/commits?per_page=1"
    $commits = Invoke-RestMethod $url -Headers @{ 'User-Agent' = 'DAXon-QT3Test' }
    return $commits[0].sha
}

$pins = Get-PinnedRevisions
$total = 0

foreach ($repo in @('qt3tests', 'xslt30-test')) {
    $revision = if ($Latest) { Get-UpstreamHead $repo } else { $pins[$repo] }
    if (-not $revision) { throw "no revision for '$repo' - corpus.pin is incomplete" }

    $target = Join-Path $root $repo
    $stamp = Join-Path $target '.corpus-revision'
    if (-not $Force -and (Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $revision)) {
        Write-Host "$repo already at $($revision.Substring(0,12)) - skipping"
        continue
    }

    $archive = Join-Path ([IO.Path]::GetTempPath()) "$repo-$revision.tar.gz"
    if (-not (Test-Path $archive)) {
        Write-Host "downloading $repo @ $($revision.Substring(0,12)) ..."
        # codeload is where github.com/<owner>/<repo>/archive/... redirects to. Ask it directly:
        # the redirecting front end returns 504 on the larger of these two archives often enough
        # to matter, while codeload serves it.
        $url = "https://codeload.github.com/w3c/$repo/tar.gz/$revision"
        $curl = Get-Command curl.exe -CommandType Application -ErrorAction SilentlyContinue
        $progress = $ProgressPreference
        $ProgressPreference = 'SilentlyContinue'   # the progress bar makes large downloads crawl
        try {
            for ($attempt = 1; ; $attempt++) {
                try {
                    if ($curl) {
                        # -f so an HTTP error is a failure rather than a saved error page
                        & $curl[0].Source -fsSL --retry 2 --retry-delay 5 -o $archive $url
                        if ($LASTEXITCODE -ne 0) { throw "curl exited with $LASTEXITCODE" }
                    }
                    else {
                        Invoke-WebRequest $url -OutFile $archive
                    }
                    break
                }
                catch {
                    if (Test-Path $archive) { Remove-Item $archive -Force }
                    if ($attempt -ge 4) { throw }
                    Write-Host "  attempt $attempt failed ($($_.Exception.Message)); retrying ..."
                    Start-Sleep -Seconds (5 * $attempt)
                }
            }
        }
        finally {
            $ProgressPreference = $progress
        }
    }

    Write-Host "extracting $repo ..."
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    New-Item -ItemType Directory -Path $target | Out-Null
    # Run from the archive's own folder and pass a bare file name: GNU tar (the one Git for Windows
    # puts on PATH) reads "C:\dir\x.tar.gz" as host:path and tries to open a network connection.
    # --strip-components drops the "<repo>-<sha>/" wrapper GitHub puts in source tarballs.
    Push-Location (Split-Path -Parent $archive)
    try {
        & tar -xzf (Split-Path -Leaf $archive) --strip-components=1 -C $target
    }
    finally {
        Pop-Location
    }
    if ($LASTEXITCODE -ne 0) { throw "tar failed for $repo" }

    Set-Content -Path $stamp -Value $revision -Encoding ascii
    Remove-Item $archive -Force
    Write-Host "$repo ready ($(Get-CorpusSize $target) MB)"
}

foreach ($repo in @('qt3tests', 'xslt30-test')) {
    $total += Get-CorpusSize (Join-Path $root $repo)
}

Write-Host ""
Write-Host "corpora ready under $root ($total MB total)"
Write-Host "run:  dotnet build tests/QT3Test -c Release"
Write-Host "      tests/QT3Test/bin/Release/net472/QT3Test.exe qt3tests xslt30-test"
