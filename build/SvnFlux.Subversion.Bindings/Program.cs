using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

const string bindingsNamespace = "SvnFlux.Subversion.Interop";
var root = FindRepositoryRoot();
var rid = args.Length == 0 ? "win-x64" : args[0];
if (rid is not ("win-x64" or "win-arm64")) {
    throw new ArgumentException($"Unsupported RID '{rid}'. Expected win-x64 or win-arm64.");
}

var architecture = rid == "win-x64" ? "amd64" : "amd64_arm64";
var target = rid == "win-x64" ? "x86_64-pc-windows-msvc" : "aarch64-pc-windows-msvc";
LoadVisualStudioEnvironment(architecture);
var install = Path.Combine(root, ".build", rid, "install");
var output = Path.Combine(root, "src", $"SvnFlux.Subversion.Interop.{rid}", "Generated");
var intermediate = Path.Combine(root, ".build", rid, "bindings");
Directory.CreateDirectory(intermediate);
RecreateDirectory(output);

var libraries = FindLibraries(install);
var exports = libraries.ToDictionary(library => library.Name, ReadExports, StringComparer.OrdinalIgnoreCase);
var duplicates = exports.SelectMany(pair => pair.Value.Select(symbol => (Symbol: symbol, Library: pair.Key)))
    .GroupBy(item => item.Symbol, StringComparer.Ordinal)
    .Where(group => group.Select(item => item.Library).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
    .ToDictionary(group => group.Key, group => group.Select(item => item.Library).Order().ToArray());
var claimedSymbols = new HashSet<string>(StringComparer.Ordinal);
var ownedExports = libraries.ToDictionary(
    library => library.Name,
    library => exports[library.Name].Where(claimedSymbols.Add).ToHashSet(StringComparer.Ordinal),
    StringComparer.OrdinalIgnoreCase);

File.WriteAllText(Path.Combine(intermediate, "exports.json"), JsonSerializer.Serialize(exports, new JsonSerializerOptions { WriteIndented = true }));
File.WriteAllText(Path.Combine(intermediate, "duplicate-exports.json"), JsonSerializer.Serialize(duplicates, new JsonSerializerOptions { WriteIndented = true }));
var headers = FindHeaders(install);
var umbrella = WriteUmbrellaHeader(headers, intermediate);
var model = GenerateModel(umbrella, headers, install, intermediate);
var forwardDeclarations = FindNestedStructs(model);
if (forwardDeclarations.Count > 0) {
    umbrella = WriteUmbrellaHeader(headers, intermediate, forwardDeclarations);
    model = GenerateModel(umbrella, headers, install, intermediate);
}
GenerateTypes(model, umbrella, headers, install, output, intermediate);
GenerateOpaqueTypes(forwardDeclarations.Concat(["sockaddr", "sockaddr_in", "sockaddr_in6", "hostent", "ldap", "_iobuf", "_SYSTEMTIME", "_FILETIME"]), output);
var generatedImports = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
foreach (var library in libraries) {
    generatedImports[library.Name] = GenerateLibrary(library, ownedExports[library.Name], umbrella, headers, install, output, intermediate);
}
var unboundExports = libraries.ToDictionary(
    library => library.Name,
    library => ownedExports[library.Name].Except(generatedImports[library.Name], StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
    StringComparer.OrdinalIgnoreCase);
File.WriteAllText(Path.Combine(intermediate, "unbound-exports.json"), JsonSerializer.Serialize(unboundExports, new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine($"Generated {generatedImports.Sum(pair => pair.Value.Count)} imports from {libraries.Count} DLLs for {rid}; {unboundExports.Sum(pair => pair.Value.Length)} exports have no public declaration and {duplicates.Count} duplicate exports were recorded.");

string GenerateModel(string header, IReadOnlyCollection<string> traversedHeaders, string installRoot, string intermediateRoot) {
    var responsePath = Path.Combine(intermediateRoot, "api-model.rsp");
    var outputPath = Path.Combine(intermediateRoot, "api-model.xml");
    var response = CreateCommonArguments(installRoot);
    response.Add("--namespace=" + bindingsNamespace);
    response.Add("--output-mode=Xml");
    response.Add("--output=" + Quote(outputPath));
    response.Add("--file=" + Quote(header));
    foreach (var traversedHeader in traversedHeaders) {
        response.Add("--traverse=" + Quote(traversedHeader));
    }
    File.WriteAllLines(responsePath, response);
    RunGenerator(["tool", "run", "ClangSharpPInvokeGenerator", "--", "@" + responsePath], outputPath);
    return outputPath;
}

List<string> FindNestedStructs(string modelPath) => XDocument.Load(modelPath).Descendants("struct")
    .Where(element => element.Parent?.Name.LocalName == "struct" && !element.Elements("field").Any())
    .Select(element => (string?)element.Attribute("name"))
    .Where(name => !string.IsNullOrWhiteSpace(name) && Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$"))
    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(name => name!).ToList();

void GenerateTypes(string modelPath, string header, IReadOnlyCollection<string> traversedHeaders, string installRoot, string outputRoot, string intermediateRoot) {
    var names = XDocument.Load(modelPath).Descendants()
        .Where(element => element.Name.LocalName is "struct" or "enumeration")
        .Select(element => (string?)element.Attribute("name"))
        .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!)
        .Concat(["sockaddr_in", "sockaddr_in6", "_SYSTEMTIME", "_FILETIME"])
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    var responsePath = Path.Combine(intermediateRoot, "types.rsp");
    var outputPath = Path.Combine(outputRoot, "Types.g.cs");
    var response = CreateCommonArguments(installRoot);
    response.Add("--namespace=" + bindingsNamespace);
    response.Add("--with-access-specifier=*=Internal");
    response.Add("--output=" + Quote(outputPath));
    response.Add("--file=" + Quote(header));
    foreach (var traversedHeader in traversedHeaders) {
        response.Add("--traverse=" + Quote(traversedHeader));
    }
    response.Add("--traverse=ws2def.h");
    response.Add("--traverse=ws2ipdef.h");
    response.Add("--traverse=minwinbase.h");
    foreach (var name in names) {
        response.Add("--include=" + name);
    }
    File.WriteAllLines(responsePath, response);
    RunGenerator(["tool", "run", "ClangSharpPInvokeGenerator", "--", "@" + responsePath], outputPath);
}

void GenerateOpaqueTypes(IEnumerable<string> names, string outputRoot) {
    var lines = new List<string> { "// <auto-generated />", "using System.Runtime.InteropServices;", "", $"namespace {bindingsNamespace};", "" };
    foreach (var name in names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) {
        lines.Add(name switch {
            "sockaddr_in" => "[StructLayout(LayoutKind.Sequential)] internal unsafe struct sockaddr_in { public short sin_family; public ushort sin_port; public fixed byte sin_addr[4]; public fixed byte sin_zero[8]; }",
            "sockaddr_in6" => "[StructLayout(LayoutKind.Sequential)] internal unsafe struct sockaddr_in6 { public ushort sin6_family; public ushort sin6_port; public uint sin6_flowinfo; public fixed byte sin6_addr[16]; public uint sin6_scope_id; }",
            "_SYSTEMTIME" => "[StructLayout(LayoutKind.Sequential)] internal unsafe struct _SYSTEMTIME { public fixed ushort values[8]; }",
            "_FILETIME" => "[StructLayout(LayoutKind.Sequential)] internal struct _FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }",
            _ => $"internal struct {name} {{ }}"
        });
    }
    File.WriteAllLines(Path.Combine(outputRoot, "OpaqueTypes.g.cs"), lines);
}

HashSet<string> GenerateLibrary(NativeLibrary library, IReadOnlyCollection<string> symbols, string umbrella, IReadOnlyCollection<string> headers, string installRoot, string outputRoot, string intermediateRoot) {
    var libraryName = Path.GetFileNameWithoutExtension(library.Name).Replace('-', '_');
    var responsePath = Path.Combine(intermediateRoot, libraryName + ".rsp");
    var outputPath = Path.Combine(outputRoot, libraryName + ".g.cs");
    var response = CreateCommonArguments(installRoot);
    response.AddRange([
        "--namespace=" + bindingsNamespace,
        "--methodClassName=" + libraryName,
        "--libraryPath=" + library.Name,
        "--with-access-specifier=*=Internal",
        "--output=" + Quote(outputPath)
    ]);
    response.Add("--file=" + Quote(umbrella));
    foreach (var header in headers) {
        response.Add("--traverse=" + Quote(header));
    }
    foreach (var symbol in symbols.Order(StringComparer.Ordinal)) {
        response.Add("--include=" + symbol);
    }
    File.WriteAllLines(responsePath, response);
    RunGenerator(["tool", "run", "ClangSharpPInvokeGenerator", "--", "@" + responsePath], outputPath);
    return Regex.Matches(File.ReadAllText(outputPath), @"public static extern .*?\b(?<name>(?:apr|apu|svn)_[A-Za-z0-9_]+)\(")
        .Select(match => match.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);
}

List<string> CreateCommonArguments(string installRoot) {
    var response = new List<string> {
        "--language=c", "--config=latest-codegen", "--config=windows-types", "--config=exclude-anonymous-field-helpers",
        "--additional=--target=" + target, "--define-macro=_WIN32=1", "--define-macro=_WIN64=1",
        "--define-macro=_WIN32_WINNT=0x0A00", "--define-macro=WINVER=0x0A00", "--define-macro=WINAPI_FAMILY=100",
        "--define-macro=" + (rid == "win-x64" ? "_AMD64_=1" : "_ARM64_=1")
    };
    foreach (var include in FindIncludeDirectories(installRoot)) {
        response.Add("--include-directory=" + Quote(include));
    }
    return response;
}

string WriteUmbrellaHeader(IEnumerable<string> headers, string intermediateRoot, IReadOnlyCollection<string>? forwardDeclarations = null) {
    var path = Path.Combine(intermediateRoot, "public-api.h");
    var lines = new[] { "#include <WinSock2.h>", "#include <Windows.h>", "#include <ws2ipdef.h>" }
        .Concat((forwardDeclarations ?? []).Select(name => $"typedef struct {name} {name};"))
        .Concat(headers.Select(header => $"#include <{Path.GetFileName(header)}>"));
    File.WriteAllLines(path, lines);
    return path;
}

List<NativeLibrary> FindLibraries(string installRoot) {
    string[] names = [
        "libapr-1.dll", "libaprutil-1.dll", "libsvn_subr-1.dll", "libsvn_delta-1.dll", "libsvn_diff-1.dll",
        "libsvn_wc-1.dll", "libsvn_ra-1.dll", "libsvn_client-1.dll", "libsvn_fs-1.dll", "libsvn_repos-1.dll"
    ];
    return names.Select(name => new NativeLibrary(name, Directory.EnumerateFiles(installRoot, name, SearchOption.AllDirectories).Single())).ToList();
}

HashSet<string> ReadExports(NativeLibrary library) {
    var result = new HashSet<string>(StringComparer.Ordinal);
    var text = Capture("dumpbin.exe", ["/nologo", "/exports", library.Path], root);
    foreach (var line in text.Split('\n')) {
        var match = Regex.Match(line, @"^\s*\d+\s+[0-9A-Fa-f]+\s+[0-9A-Fa-f]+\s+(?<name>\S+)");
        if (match.Success) {
            result.Add(match.Groups["name"].Value);
        }
    }
    if (result.Count == 0) {
        throw new InvalidOperationException($"No exports found in {library.Path}.");
    }
    return result;
}

List<string> FindHeaders(string installRoot) {
    var svn = Path.Combine(installRoot, "subversion", "include", "subversion-1");
    var apr = Path.Combine(installRoot, "apr", "include", "apr-1");
    return Directory.EnumerateFiles(svn, "svn_*.h").Concat(Directory.EnumerateFiles(apr, "*.h"))
        .Where(path => !Path.GetFileName(path).Contains("private", StringComparison.OrdinalIgnoreCase))
        .Order(StringComparer.OrdinalIgnoreCase).ToList();
}

IEnumerable<string> FindIncludeDirectories(string installRoot) {
    var nativeIncludes = new[] {
        Path.Combine(installRoot, "subversion", "include", "subversion-1"),
        Path.Combine(installRoot, "apr", "include", "apr-1")
    };
    var visualStudioIncludes = (Environment.GetEnvironmentVariable("INCLUDE") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    return nativeIncludes.Concat(visualStudioIncludes).Distinct(StringComparer.OrdinalIgnoreCase);
}

void LoadVisualStudioEnvironment(string targetArchitecture) {
    var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
    var installation = Capture(vswhere, ["-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"], root).Trim();
    var vcvars = Path.Combine(installation, "VC", "Auxiliary", "Build", "vcvarsall.bat");
    var environment = CaptureCommand($"\"{vcvars}\" {targetArchitecture} >nul && set");
    foreach (var line in environment.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)) {
        var separator = line.IndexOf('=');
        if (separator > 0) {
            Environment.SetEnvironmentVariable(line[..separator], line[(separator + 1)..]);
        }
    }
}

string CaptureCommand(string command) {
    var startInfo = new ProcessStartInfo("cmd.exe") {
        Arguments = $"/d /s /c \"{command}\"",
        WorkingDirectory = root,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start cmd.exe.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    Task.WaitAll(stdout, stderr);
    if (process.ExitCode != 0) {
        throw new InvalidOperationException($"cmd.exe exited with code {process.ExitCode}: {stderr.Result}");
    }
    return stdout.Result;
}

string Capture(string fileName, IEnumerable<string> arguments, string workingDirectory) {
    var startInfo = CreateStartInfo(fileName, arguments, workingDirectory, true);
    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    Task.WaitAll(stdout, stderr);
    if (process.ExitCode != 0) {
        throw new InvalidOperationException($"{fileName} exited with code {process.ExitCode}: {stderr.Result}");
    }
    return stdout.Result;
}

void RunGenerator(IEnumerable<string> arguments, string outputPath) {
    Console.WriteLine($"> dotnet {string.Join(' ', arguments)}");
    var startInfo = CreateStartInfo("dotnet", arguments, root, false);
    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start dotnet.");
    process.WaitForExit();
    if (process.ExitCode < 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0) {
        throw new InvalidOperationException($"ClangSharp exited with code {process.ExitCode} and did not produce valid output.");
    }
    if (process.ExitCode != 0) {
        Console.WriteLine($"ClangSharp completed with {process.ExitCode} binding warnings.");
    }
}

ProcessStartInfo CreateStartInfo(string fileName, IEnumerable<string> arguments, string workingDirectory, bool redirect) {
    var info = new ProcessStartInfo(fileName) { WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = redirect, RedirectStandardError = redirect };
    foreach (var argument in arguments) {
        info.ArgumentList.Add(argument);
    }
    return info;
}

string FindRepositoryRoot() {
    var directory = new DirectoryInfo(Environment.CurrentDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SvnFlux.Subversion.Native.slnx"))) {
        directory = directory.Parent;
    }
    return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
}

void RecreateDirectory(string path) {
    if (Directory.Exists(path)) {
        Directory.Delete(path, true);
    }
    Directory.CreateDirectory(path);
}

string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

sealed record NativeLibrary(string Name, string Path);
