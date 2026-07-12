using SvnFlux.Subversion.Native.Build;

var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
var skipDependencies = args.Contains("--skip-dependencies", StringComparer.OrdinalIgnoreCase);
var noPack = args.Contains("--no-pack", StringComparer.OrdinalIgnoreCase);
var version = GetOption(args, "--version");
var target = BuildTarget.Parse(GetOption(args, "--rid") ?? "win-x64");

try {
    var builder = new NativeBuilder(repositoryRoot, target, skipDependencies);
    await builder.BuildAsync(CancellationToken.None);
    if (!noPack) {
        builder.Pack(version);
    }
    return 0;
} catch (Exception exception) {
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static string FindRepositoryRoot(string start) {
    for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent) {
        if (File.Exists(Path.Combine(directory.FullName, "SvnFlux.Subversion.Native.slnx"))) {
            return directory.FullName;
        }
    }
    throw new InvalidOperationException("Could not find the SvnFlux repository root.");
}

static string? GetOption(string[] arguments, string option) {
    var index = Array.FindIndex(arguments, argument => string.Equals(argument, option, StringComparison.OrdinalIgnoreCase));
    if (index < 0) {
        return null;
    }
    if (index == arguments.Length - 1) {
        throw new ArgumentException($"{option} requires a value.");
    }
    return arguments[index + 1];
}
