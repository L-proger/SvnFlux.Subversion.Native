param(
    [ValidateSet("win-x64", "win-arm64", "all")]
    [string]$Rid = "all"
)

$root = Split-Path $PSScriptRoot -Parent
$rids = if ($Rid -eq "all") { "win-x64", "win-arm64" } else { $Rid }
foreach ($target in $rids) {
    & dotnet run --project "$root\build\SvnFlux.Subversion.Bindings\SvnFlux.Subversion.Bindings.csproj" -c Release -- $target
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
