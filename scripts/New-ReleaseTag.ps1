[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$catalogPath = Join-Path $root 'build/SvnFlux.Subversion.Native.Build/DependencyCatalog.cs'

function Invoke-Git([string[]] $Arguments) {
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& git -C $root @Arguments 2>&1) | ForEach-Object { $_.ToString() }
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($exitCode -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $exitCode`n$($output -join [Environment]::NewLine)"
    }
    return $output
}

$catalog = Get-Content $catalogPath -Raw
$versionMatch = [regex]::Match($catalog, 'SubversionVersion\s*=\s*"([^"]+)"')
$commitMatch = [regex]::Match($catalog, 'SubversionCommit\s*=\s*"([0-9a-fA-F]{40})"')
if (-not $versionMatch.Success -or -not $commitMatch.Success) {
    throw 'Could not read SubversionVersion and SubversionCommit from DependencyCatalog.cs.'
}

$version = $versionMatch.Groups[1].Value
$commit = $commitMatch.Groups[1].Value.ToLowerInvariant()
$shortCommit = $commit.Substring(0, 7)
$date = [DateTime]::UtcNow.ToString('yyyyMMdd')

$insideWorkTree = (Invoke-Git @('rev-parse', '--is-inside-work-tree')) -join ''
if ($insideWorkTree -ne 'true') {
    throw "$root is not a Git working tree."
}
if (@(Invoke-Git @('status', '--porcelain')).Count -ne 0) {
    throw 'The working tree is not clean. Commit all release changes first.'
}
if (-not ((Invoke-Git @('remote')) -contains 'origin')) {
    throw "Git remote 'origin' is not configured."
}

Invoke-Git @('fetch', 'origin', '--tags') | Out-Null

$prefix = "svn-$version.$date.$shortCommit"
$revisions = Invoke-Git @('tag', '--list', "$prefix.*") | ForEach-Object {
    if ($_ -match "^$([regex]::Escape($prefix))\.(\d+)$") {
        [int] $Matches[1]
    }
}
$revision = if ($revisions) { ($revisions | Measure-Object -Maximum).Maximum + 1 } else { 1 }
$tag = "$prefix.$revision"
$head = (Invoke-Git @('rev-parse', 'HEAD')) -join ''
$message = "Apache Subversion $version ($shortCommit), SvnFlux native package revision $revision"

Write-Host "Release commit: $head"
Write-Host "SVN commit:     $commit"
Write-Host "Release tag:    $tag"

if ($PSCmdlet.ShouldProcess($head, "Create and push tag $tag")) {
    Invoke-Git @('tag', '--annotate', $tag, '--message', $message) | Out-Null
    try {
        Invoke-Git @('push', 'origin', "refs/tags/$tag") | Out-Null
    } catch {
        Invoke-Git @('tag', '--delete', $tag) | Out-Null
        throw
    }
    Write-Host "Pushed $tag to origin."
}
