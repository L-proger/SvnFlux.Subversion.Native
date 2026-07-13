using System.Collections;
using System.Diagnostics;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace SvnFlux.Subversion.Native.Build;

internal static class Infrastructure {
    private static readonly Dictionary<string, string> BaseEnvironment = CaptureCurrentEnvironment();
    private static readonly HashSet<string> VisualStudioVariables = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> ToolDirectories = [];

    public static async Task<string> DownloadAndExtractAsync(SourceArchive source, string downloadsDirectory, string sourcesDirectory, CancellationToken cancellationToken) {
        var destination = Path.Combine(sourcesDirectory, source.DirectoryName);
        if (Directory.Exists(destination)) {
            Console.WriteLine($"Using {source.DirectoryName}");
            return destination;
        }

        Directory.CreateDirectory(downloadsDirectory);
        Directory.CreateDirectory(sourcesDirectory);
        var archivePath = Path.Combine(downloadsDirectory, source.FileName);
        if (!File.Exists(archivePath)) {
            Console.WriteLine($"Downloading {source.Url}");
            var temporaryPath = archivePath + ".download";
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            using var response = await client.GetAsync(source.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None)) {
                await input.CopyToAsync(output, cancellationToken);
            }
            File.Move(temporaryPath, archivePath, true);
        }

        Console.WriteLine($"Extracting {source.FileName}");
        var extractionRoot = source.ExtractIntoNamedDirectory ? destination : sourcesDirectory;
        Directory.CreateDirectory(extractionRoot);
        using var archiveStream = File.OpenRead(archivePath);
        using var reader = ReaderFactory.OpenReader(archiveStream);
        while (reader.MoveToNextEntry()) {
            if (!reader.Entry.IsDirectory) {
                reader.WriteEntryToDirectory(extractionRoot, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
            }
        }
        if (!Directory.Exists(destination)) {
            throw new InvalidOperationException($"Archive {source.FileName} did not produce {destination}.");
        }
        return destination;
    }

    public static string RequireTool(string name) {
        var candidates = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? [name] : new[] { name, name + ".exe" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) {
            foreach (var candidate in candidates) {
                var path = Path.Combine(directory.Trim(), candidate);
                if (File.Exists(path)) {
                    return Path.GetFullPath(path);
                }
            }
        }
        throw new InvalidOperationException($"Required tool '{name}' was not found in PATH.");
    }

    public static void LoadVisualStudioEnvironment(string architecture) {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var vswhere = Path.Combine(programFiles, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) {
            throw new InvalidOperationException("Visual Studio Installer's vswhere.exe was not found.");
        }
        var installation = Capture(vswhere, ["-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"]).Trim();
        if (installation.Length == 0) {
            throw new InvalidOperationException("Visual Studio with the x64 C++ toolchain was not found.");
        }
        var vcvars = Path.Combine(installation, "VC", "Auxiliary", "Build", "vcvarsall.bat");
        var output = CaptureCommand($"\"{vcvars}\" {architecture} >nul && set", BaseEnvironment);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)) {
            var separator = line.IndexOf('=');
            if (separator > 0) {
                environment[line[..separator]] = line[(separator + 1)..];
            }
        }
        foreach (var name in VisualStudioVariables) {
            Environment.SetEnvironmentVariable(name, null);
        }
        VisualStudioVariables.Clear();
        foreach (var (name, value) in environment) {
            Environment.SetEnvironmentVariable(name, value);
            VisualStudioVariables.Add(name);
        }
        ApplyToolDirectories();
    }

    public static void AddToolDirectory(string path) {
        if (!ToolDirectories.Contains(path, StringComparer.OrdinalIgnoreCase)) {
            ToolDirectories.Add(path);
        }
        ApplyToolDirectories();
    }

    public static void Run(string fileName, IEnumerable<string> arguments, string workingDirectory) {
        Console.WriteLine($"> {Path.GetFileName(fileName)} {string.Join(' ', arguments)}");
        var startInfo = CreateStartInfo(fileName, arguments, workingDirectory, redirectOutput: false);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        process.WaitForExit();
        if (process.ExitCode != 0) {
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} exited with code {process.ExitCode}.");
        }
    }

    public static string Capture(string fileName, IEnumerable<string> arguments) {
        var startInfo = CreateStartInfo(fileName, arguments, Environment.CurrentDirectory, redirectOutput: true);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) {
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} exited with code {process.ExitCode}: {error}");
        }
        return output;
    }

    private static string CaptureCommand(string command, IReadOnlyDictionary<string, string> environment) {
        var startInfo = new ProcessStartInfo("cmd.exe") {
            Arguments = $"/d /s /c \"{command}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment.Clear();
        foreach (var (name, value) in environment) {
            startInfo.Environment[name] = value;
        }
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start cmd.exe.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) {
            throw new InvalidOperationException($"cmd.exe exited with code {process.ExitCode}: {error}");
        }
        return output;
    }

    private static Dictionary<string, string> CaptureCurrentEnvironment() {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables()) {
            if (variable.Key is string name && variable.Value is string value) {
                result[name] = value;
            }
        }
        return result;
    }

    private static void ApplyToolDirectories() {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var entries = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(entry => !ToolDirectories.Contains(entry, StringComparer.OrdinalIgnoreCase));
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, ToolDirectories.Concat(entries)));
    }

    public static string FreshDirectory(string parent, string name) {
        var path = Path.Combine(parent, name);
        if (Directory.Exists(path)) {
            Directory.Delete(path, true);
        }
        Directory.CreateDirectory(path);
        return path;
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, IEnumerable<string> arguments, string workingDirectory, bool redirectOutput) {
        var startInfo = new ProcessStartInfo(fileName) {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput
        };
        foreach (var argument in arguments) {
            startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }
}
