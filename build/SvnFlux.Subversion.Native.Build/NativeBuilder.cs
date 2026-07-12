namespace SvnFlux.Subversion.Native.Build;

internal sealed class NativeBuilder {
    private readonly string repositoryRoot;
    private readonly BuildTarget target;
    private readonly bool skipDependencies;
    private readonly string nativeRoot;
    private readonly string artifactsRoot;

    public NativeBuilder(string repositoryRoot, BuildTarget target, bool skipDependencies) {
        this.repositoryRoot = repositoryRoot;
        this.target = target;
        this.skipDependencies = skipDependencies;
        nativeRoot = repositoryRoot;
        artifactsRoot = Path.Combine(repositoryRoot, "artifacts");
    }

    private string CacheRoot => Path.Combine(nativeRoot, ".build");
    private string BuildRoot => Path.Combine(CacheRoot, target.Rid);
    private string InstallRoot => Path.Combine(BuildRoot, "install");

    public async Task BuildAsync(CancellationToken cancellationToken) {
        if (!OperatingSystem.IsWindows()) {
            throw new PlatformNotSupportedException("This builder currently supports Windows targets only.");
        }
        Infrastructure.LoadVisualStudioEnvironment(target.VcVarsArchitecture);
        PrepareScons();
        RequireBuildTools();

        var downloads = Path.Combine(CacheRoot, "downloads");
        var sources = Path.Combine(BuildRoot, "sources");
        var sourcePaths = new Dictionary<SourceArchive, string>();
        foreach (var dependency in DependencyCatalog.All) {
            sourcePaths[dependency] = await Infrastructure.DownloadAndExtractAsync(dependency, downloads, sources, cancellationToken);
        }

        if (!skipDependencies && Directory.Exists(InstallRoot)) {
            Directory.Delete(InstallRoot, true);
        }
        Directory.CreateDirectory(InstallRoot);
        var openssl = GetOpenSsl();
        var zlib = BuildZlib(sourcePaths[DependencyCatalog.Zlib]);
        var expat = BuildExpat(sourcePaths[DependencyCatalog.Expat]);
        var apr = BuildApr(sourcePaths[DependencyCatalog.Apr]);
        BuildAprUtil(sourcePaths[DependencyCatalog.AprUtil], expat, openssl, apr);
        PrepareAprForSerf(apr);
        var serf = BuildSerf(sourcePaths[DependencyCatalog.Serf], apr, zlib, openssl);
        var subversion = BuildSubversion(sourcePaths[DependencyCatalog.Subversion], sourcePaths[DependencyCatalog.Sqlite], expat, apr, serf, zlib, openssl);
        GatherRuntime(subversion, apr, serf, zlib, openssl, expat);
    }

    public void Pack(string? version) {
        var projectName = "SvnFlux.Subversion.Native." + target.Rid;
        var project = Path.Combine(nativeRoot, "src", projectName, projectName + ".csproj");
        var output = Path.Combine(artifactsRoot, "packages");
        var nativeOutput = Path.Combine(artifactsRoot, "native", target.Rid);
        Directory.CreateDirectory(output);
        var arguments = new List<string> { "pack", project, "-c", "Release", "--output", output, $"-p:NativeOutput={nativeOutput}" };
        if (!string.IsNullOrWhiteSpace(version)) {
            arguments.Add($"-p:PackageVersion={version}");
        }
        Infrastructure.Run(Infrastructure.RequireTool("dotnet"), arguments, repositoryRoot);
    }

    private string BuildZlib(string source) {
        var install = Path.Combine(InstallRoot, "zlib");
        if (!skipDependencies) {
            CmakeBuild("zlib", source, install);
            CopyCompatibilityLibrary(install, "z.lib", "zlib.lib");
            CopyCompatibilityLibrary(install, "zs.lib", "zlibstatic.lib");
        }
        return install;
    }

    private string BuildExpat(string source) {
        var install = Path.Combine(InstallRoot, "expat");
        if (!skipDependencies) {
            CmakeBuild("expat", source, install, "-DEXPAT_BUILD_DOCS=OFF", "-DEXPAT_BUILD_EXAMPLES=OFF", "-DEXPAT_BUILD_TESTS=OFF", "-DEXPAT_BUILD_TOOLS=OFF", "-DEXPAT_SHARED_LIBS=ON");
        }
        return install;
    }

    private string BuildApr(string source) {
        var install = Path.Combine(InstallRoot, "apr");
        if (!skipDependencies) {
            if (target.Rid == "win-arm64") {
                PrepareAprCrossBuild(source);
            }
            CmakeBuild("apr", source, install, "-DAPR_INSTALL_PRIVATE_H=ON", "-DAPR_BUILD_STATIC=ON");
        }
        return install;
    }

    private void PrepareAprCrossBuild(string source) {
        Infrastructure.LoadVisualStudioEnvironment("amd64");
        var hostBuild = Infrastructure.FreshDirectory(Path.Combine(CacheRoot, "host-tools"), "apr-1.7.6");
        var generator = Path.Combine(hostBuild, "gen_test_char.exe");
        Infrastructure.Run(Infrastructure.RequireTool("cl"), ["/nologo", $"/Fe:{generator}", Path.Combine(source, "tools", "gen_test_char.c")], hostBuild);
        var header = Path.Combine(hostBuild, "apr_escape_test_char.h");
        File.WriteAllText(header, Infrastructure.Capture(generator, []));

        var cmakeLists = Path.Combine(source, "CMakeLists.txt");
        var replacement = $"CONFIGURE_FILE(\"{Forward(header)}\" ${{PROJECT_BINARY_DIR}}/apr_escape_test_char.h COPYONLY){Environment.NewLine}ADD_CUSTOM_TARGET(test_char_header ALL DEPENDS ${{PROJECT_BINARY_DIR}}/apr_escape_test_char.h)";
        var text = File.ReadAllText(cmakeLists);
        var start = text.IndexOf("ADD_EXECUTABLE(gen_test_char tools/gen_test_char.c)", StringComparison.Ordinal);
        if (start < 0) {
            start = text.IndexOf("CONFIGURE_FILE(\"", text.IndexOf("CONFIGURE_FILE(include/apr.hwc", StringComparison.Ordinal) + 1, StringComparison.Ordinal);
        }
        var end = text.IndexOf("# Generated .h files", start, StringComparison.Ordinal);
        if (start < 0 || end < 0) {
            throw new InvalidOperationException("The expected APR gen_test_char CMake block was not found.");
        }
        File.WriteAllText(cmakeLists, text[..start] + replacement + Environment.NewLine + Environment.NewLine + text[end..]);
        Infrastructure.LoadVisualStudioEnvironment(target.VcVarsArchitecture);
    }

    private void BuildAprUtil(string source, string expat, string openssl, string apr) {
        if (skipDependencies) {
            return;
        }
        CmakeBuild("apr-util", source, apr, "-DAPR_INSTALL_PRIVATE_H=ON", $"-DEXPAT_LIBRARY={Forward(Path.Combine(expat, "lib", "libexpat.lib"))}", $"-DEXPAT_INCLUDE_DIR={Forward(Path.Combine(expat, "include"))}", "-DAPU_HAVE_CRYPTO=ON", $"-DOPENSSL_ROOT_DIR={Forward(openssl)}");
    }

    private string BuildSerf(string source, string apr, string zlib, string openssl) {
        var install = Path.Combine(InstallRoot, "serf");
        if (!skipDependencies) {
            var sconstruct = Path.Combine(source, "SConstruct");
            var text = File.ReadAllText(sconstruct);
            text = text
                .Replace("$OPENSSL/include/openssl", "$OPENSSL/include", StringComparison.Ordinal)
                .Replace("allowed_values=('x86', 'x86_64', 'ia64')", "allowed_values=('x86', 'x86_64', 'arm64', 'ia64')", StringComparison.Ordinal)
                .Replace("env.get('TARGET_ARCH', None) == 'x86_64'", "env.get('TARGET_ARCH', None) in ('x86_64', 'arm64')", StringComparison.Ordinal);
            if (!text.Contains("'ARM64': 'arm64'", StringComparison.Ordinal)) {
                text = text.Replace("'X64'  : 'x86_64'", "'X64'  : 'x86_64',\n                      'ARM64': 'arm64',\n                      'arm64': 'arm64'", StringComparison.Ordinal);
            }
            File.WriteAllText(sconstruct, text);
            Infrastructure.RequireTool("cl");
            var common = new[] { $"APR={Forward(apr)}", $"APU={Forward(apr)}", $"ZLIB={Forward(zlib)}", $"OPENSSL={Forward(openssl)}", "SOURCE_LAYOUT=0", $"TARGET_ARCH={target.SconsArchitecture}", $"PREFIX={Forward(install)}", $"LIBDIR={Forward(Path.Combine(install, "lib"))}" };
            Infrastructure.Run(Infrastructure.RequireTool("scons"), common, source);
            Infrastructure.Run(Infrastructure.RequireTool("scons"), [.. common, "install"], source);
        }
        return install;
    }

    private string BuildSubversion(string source, string sqlite, string expat, string apr, string serf, string zlib, string openssl) {
        var install = Path.Combine(InstallRoot, "subversion");
        var generatedTargets = Path.Combine(source, "build", "cmake", "targets.cmake");
        if (!File.Exists(generatedTargets)) {
            Infrastructure.Run(Infrastructure.RequireTool("python"), ["gen-make.py", "-t", "cmake"], source);
        }
        CmakeBuild("subversion", source, install,
            $"-DCMAKE_LIBRARY_PATH={Forward(apr)};{Forward(serf)};{Forward(zlib)}",
            $"-DCMAKE_PREFIX_PATH={Forward(apr)};{Forward(serf)};{Forward(zlib)}",
            $"-DPC_EXPAT_LIBRARY_DIRS={Forward(Path.Combine(expat, "lib"))}",
            $"-DPC_EXPAT_INCLUDE_DIRS={Forward(Path.Combine(expat, "include"))}",
            "-DSVN_SQLITE_USE_AMALGAMATION=ON", $"-DSQLITE_AMALGAMATION_DIR={Forward(sqlite)}",
            "-DSVN_ENABLE_RA_SERF=ON", $"-DOPENSSL_ROOT_DIR={Forward(openssl)}", "-DSVN_ENABLE_SVNXX=ON", "-DSVN_ENABLE_NLS=OFF", "-DZLIB_USE_STATIC_LIBS=ON");
        return install;
    }

    private void CmakeBuild(string name, string source, string install, params string[] options) {
        var build = Infrastructure.FreshDirectory(BuildRoot, "build-" + name);
        Infrastructure.Run(Infrastructure.RequireTool("cmake"), ["-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release", "-DCMAKE_POLICY_VERSION_MINIMUM=3.5", $"-DCMAKE_INSTALL_PREFIX={Forward(install)}", .. options, source], build);
        Infrastructure.Run(Infrastructure.RequireTool("ninja"), [], build);
        Infrastructure.Run(Infrastructure.RequireTool("ninja"), ["install"], build);
    }

    private void PrepareAprForSerf(string apr) {
        if (skipDependencies) {
            return;
        }
        var include = Path.Combine(apr, "include");
        var compatibilityDirectory = Path.Combine(include, "apr-1");
        Directory.CreateDirectory(compatibilityDirectory);
        foreach (var file in Directory.EnumerateFiles(include)) {
            File.Copy(file, Path.Combine(compatibilityDirectory, Path.GetFileName(file)), true);
        }
    }

    private void GatherRuntime(params string[] installations) {
        var output = Path.Combine(artifactsRoot, "native", target.Rid);
        if (Directory.Exists(output)) {
            Directory.Delete(output, true);
        }
        Directory.CreateDirectory(output);
        foreach (var installation in installations) {
            foreach (var file in Directory.EnumerateFiles(installation, "*.dll", SearchOption.AllDirectories)) {
                File.Copy(file, Path.Combine(output, Path.GetFileName(file)), true);
            }
        }
        var clientLibrary = Directory.EnumerateFiles(output, "*svn_client-1*.dll").FirstOrDefault();
        if (clientLibrary is null) {
            throw new InvalidOperationException("The build completed without an svn_client runtime DLL.");
        }
        Console.WriteLine($"Native runtime: {output}");
    }

    private static string Forward(string path) => path.Replace('\\', '/');

    private static void CopyCompatibilityLibrary(string install, string sourceName, string destinationName) {
        var libraryDirectory = Path.Combine(install, "lib");
        var source = Path.Combine(libraryDirectory, sourceName);
        if (!File.Exists(source)) {
            throw new InvalidOperationException($"zlib did not install {sourceName}.");
        }
        File.Copy(source, Path.Combine(libraryDirectory, destinationName), true);
    }

    private void PrepareScons() {
        const string version = "4.10.1";
        var environment = Path.Combine(CacheRoot, "tools", "scons-" + version);
        var scripts = Path.Combine(environment, "Scripts");
        var executable = Path.Combine(scripts, "scons.exe");
        if (!File.Exists(executable)) {
            Infrastructure.FreshDirectory(Path.GetDirectoryName(environment)!, Path.GetFileName(environment));
            var python = Infrastructure.RequireTool("python");
            Infrastructure.Run(python, ["-m", "venv", environment], repositoryRoot);
            var isolatedPython = Path.Combine(scripts, "python.exe");
            Infrastructure.Run(isolatedPython, ["-m", "pip", "install", "--disable-pip-version-check", "--no-input", "--only-binary=:all:", "SCons==" + version], repositoryRoot);
        }
        Infrastructure.AddToolDirectory(scripts);
    }

    private static void RequireBuildTools() {
        foreach (var tool in new[] { "cmake", "ninja", "python", "nmake", "scons", "dotnet" }) {
            Infrastructure.RequireTool(tool);
        }
    }

    private string GetOpenSsl() {
        var root = Path.Combine(AppContext.BaseDirectory, "tools", "openssl", target.Rid);
        var runtime = $"libssl-3-{target.OpenSslDllSuffix}.dll";
        if (!File.Exists(Path.Combine(root, "include", "openssl", "ssl.h")) || !File.Exists(Path.Combine(root, "lib", "libssl.lib")) || !File.Exists(Path.Combine(root, "bin", runtime))) {
            throw new InvalidOperationException($"The openssl-native {target.Rid} assets were not copied to the build tool output.");
        }
        return root;
    }
}
