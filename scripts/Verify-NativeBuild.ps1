param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Rid
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$native = Join-Path $root "artifacts/native/$Rid"
$expectedMachine = if ($Rid -eq 'win-x64') { 0x8664 } else { 0xAA64 }

if (-not (Test-Path (Join-Path $native 'libsvn_client-1.dll'))) {
    throw "Missing native runtime for $Rid."
}

function Get-PeMachine([string] $Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        $stream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset + 4
        return $reader.ReadUInt16()
    } finally {
        $stream.Dispose()
    }
}

$binaries = @(Get-ChildItem $native -File | Where-Object Extension -In '.dll', '.exe')
if ($binaries.Count -eq 0) {
    throw "No PE files found for $Rid."
}

$invalid = @($binaries | Where-Object { (Get-PeMachine $_.FullName) -ne $expectedMachine })
if ($invalid.Count -ne 0) {
    throw "Unexpected PE architecture: $($invalid.Name -join ', ')."
}

if ($Rid -eq 'win-x64') {
    $svn = Join-Path $root '.build/win-x64/install/subversion/bin/svn.exe'
    if (-not (Test-Path $svn)) {
        throw 'Built svn.exe was not found.'
    }
    $env:PATH = "$native;$env:PATH"
    & $svn --version --quiet
    if ($LASTEXITCODE -ne 0) {
        throw "svn.exe exited with code $LASTEXITCODE."
    }
}

Write-Host "Verified $($binaries.Count) $Rid PE files."

