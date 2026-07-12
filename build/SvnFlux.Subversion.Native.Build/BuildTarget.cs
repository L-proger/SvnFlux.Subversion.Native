namespace SvnFlux.Subversion.Native.Build;

internal sealed record BuildTarget(string Rid, string VcVarsArchitecture, string SconsArchitecture, string OpenSslDllSuffix) {
    public static BuildTarget Parse(string rid) => rid.ToLowerInvariant() switch {
        "win-x64" => new("win-x64", "amd64", "X64", "x64"),
        "win-arm64" => new("win-arm64", "amd64_arm64", "ARM64", "arm64"),
        _ => throw new ArgumentException($"Unsupported RID '{rid}'. Supported values: win-x64, win-arm64.", nameof(rid))
    };
}
