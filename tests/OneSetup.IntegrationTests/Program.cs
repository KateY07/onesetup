using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace OneSetup.IntegrationTests;

static class Program
{
    static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("These integration tests require Windows.");
            return 2;
        }

        if (args.Length is < 1 or > 2)
        {
            Console.Error.WriteLine("Usage: OneSetup.IntegrationTests <path-to-onesetup.exe> [path-to-7z.exe]");
            return 2;
        }

        string packerPath = Path.GetFullPath(args[0]);
        if (!File.Exists(packerPath))
            throw new FileNotFoundException("The packer executable was not found.", packerPath);
        string sevenZipPath = ResolveSevenZipPath(args.Length == 2 ? args[1] : null, packerPath);

        string testRoot = Path.Combine(Path.GetTempPath(), $"onesetup-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);

        try
        {
            await RunIntegrationTestAsync(packerPath, sevenZipPath, testRoot);
            Console.WriteLine("PASS: OneSetup SFX integration test");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: {exception}");
            return 1;
        }
        finally
        {
            try
            {
                if (Environment.GetEnvironmentVariable("ONESETUP_KEEP_TEST_ROOT") == "1")
                    Console.Error.WriteLine($"Preserved test directory: {testRoot}");
                else if (Directory.Exists(testRoot))
                    await DeleteTestDirectoryAsync(testRoot);
            }
            catch (Exception cleanupException)
            {
                Console.Error.WriteLine($"WARNING: Could not remove test directory '{testRoot}': {cleanupException.Message}");
            }
        }
    }

    static async Task RunIntegrationTestAsync(string packerPath, string sevenZipPath, string testRoot)
    {
        string sourceDirectory = Path.Combine(testRoot, "source");
        string runnerDirectory = Path.Combine(testRoot, "runner");
        string setupPath = Path.Combine(runnerDirectory, "fixture_setup.exe");
        string batchMarkerPath = Path.Combine(testRoot, "install-bat.marker");
        string extractedDirectory = Path.Combine(testRoot, "extracted-by-7z");
        Directory.CreateDirectory(runnerDirectory);
        Directory.CreateDirectory(extractedDirectory);
        CreateFixture(sourceDirectory);
        Manifest expectedManifest = CaptureManifest(sourceDirectory);

        ProcessResult packResult = await RunProcessAsync(
            packerPath,
            [sourceDirectory, "-o", setupPath],
            runnerDirectory,
            timeout: TestTimeout);
        Ensure(packResult.ExitCode == 0, $"Packing failed.\nSTDOUT:\n{packResult.StandardOutput}\nSTDERR:\n{packResult.StandardError}");
        Ensure(File.Exists(setupPath), "The setup executable was not created.");
        AssertSfxConfiguration(setupPath);

        ProcessResult extractResult = await RunProcessAsync(
            sevenZipPath,
            ["x", setupPath, "-y", $"-o{extractedDirectory}"],
            runnerDirectory,
            timeout: TestTimeout);
        Ensure(extractResult.ExitCode == 0, $"7-Zip could not extract the generated SFX.\nSTDOUT:\n{extractResult.StandardOutput}\nSTDERR:\n{extractResult.StandardError}");
        AssertManifestEqual(expectedManifest, CaptureManifest(extractedDirectory));

        ProcessResult collisionResult = await RunProcessAsync(
            packerPath,
            [sourceDirectory, "-o", setupPath],
            runnerDirectory,
            timeout: TestTimeout);
        Ensure(collisionResult.ExitCode != 0, "A second package operation overwrote an existing output file.");
        Ensure(collisionResult.StandardError.Contains("already exists", StringComparison.OrdinalIgnoreCase), "The output collision error was not reported.");

        ProcessResult sfxResult = await RunProcessAsync(
            setupPath,
            ["-y"],
            runnerDirectory,
            new Dictionary<string, string?>
            {
                ["ONESETUP_TEST_BAT_RESULT"] = batchMarkerPath
            },
            TestTimeout);
        Ensure(sfxResult.ExitCode == 0, $"SFX execution failed with exit code {sfxResult.ExitCode}.\nSTDOUT:\n{sfxResult.StandardOutput}\nSTDERR:\n{sfxResult.StandardError}");
        Ensure(File.Exists(batchMarkerPath), "install.bat was not executed.");
        AssertBatchEnvironment(batchMarkerPath);
        AssertManifestEqual(expectedManifest, CaptureManifest(sourceDirectory));
    }

    static void CreateFixture(string sourceDirectory)
    {
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "nested"));
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "empty-directory"));
        WriteText(Path.Combine(sourceDirectory, "payload.txt"), "payload\r\n");
        WriteText(Path.Combine(sourceDirectory, "nested", "config.json"), "{\"enabled\":true}\r\n");
        WriteText(Path.Combine(sourceDirectory, "unicode-测试.txt"), "unicode payload\r\n");
        WriteText(Path.Combine(sourceDirectory, "install.bat"), "@echo off\r\nsetlocal\r\n> \"%ONESETUP_TEST_BAT_RESULT%\" echo install-bat-ran\r\n>> \"%ONESETUP_TEST_BAT_RESULT%\" echo current-directory=%CD%\r\n>> \"%ONESETUP_TEST_BAT_RESULT%\" echo script-directory=%~dp0\r\nendlocal\r\nexit /b 0\r\n");
    }

    static void AssertSfxConfiguration(string setupPath)
    {
        string packageText = Encoding.UTF8.GetString(File.ReadAllBytes(setupPath));
        Ensure(packageText.Contains("ExecuteFile=\"install.bat\"", StringComparison.Ordinal), "The generated SFX does not execute install.bat directly.");
        Ensure(!packageText.Contains("RunProgram=\"cmd.exe", StringComparison.Ordinal), "The generated SFX still uses the broken RunProgram cmd.exe path.");
    }

    static void AssertBatchEnvironment(string reportPath)
    {
        string[] lines = File.ReadAllLines(reportPath);
        Ensure(lines.Length >= 3, "install.bat did not write its complete environment report.");
        Ensure(lines[0].Trim() == "install-bat-ran", "install.bat marker content is incorrect.");
        string currentDirectory = ValueAfter(lines, "current-directory=");
        string scriptDirectory = ValueAfter(lines, "script-directory=").TrimEnd('\\', '/');
        Ensure(PathsEqual(currentDirectory, scriptDirectory), $"The working directory was not the extracted root. Current='{currentDirectory}', ScriptRoot='{scriptDirectory}'.");
        Ensure(IsUnderDirectory(currentDirectory, Path.GetTempPath()), $"The batch file did not run from a temporary extraction directory: {currentDirectory}");
    }

    static string ValueAfter(IEnumerable<string> lines, string prefix)
    {
        string? line = lines.FirstOrDefault(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        Ensure(line is not null, $"The install.bat report is missing '{prefix}'.");
        return line![prefix.Length..].Trim();
    }

    static string ResolveSevenZipPath(string? argument, string packerPath)
    {
        if (!string.IsNullOrWhiteSpace(argument))
        {
            string path = Path.GetFullPath(argument);
            if (!File.Exists(path))
                throw new FileNotFoundException("The 7-Zip executable was not found.", path);
            return path;
        }

        string[] candidates =
        [
            Path.Combine(Environment.CurrentDirectory, "vendor", "7z.exe"),
            Path.Combine(Path.GetDirectoryName(packerPath) ?? Environment.CurrentDirectory, "7z.exe")
        ];
        string? candidate = candidates.FirstOrDefault(File.Exists);
        return candidate ?? throw new FileNotFoundException("The 7-Zip executable was not found. Pass its path as the second argument.");
    }

    static Manifest CaptureManifest(string root)
    {
        List<FileEntry> files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => new FileEntry(RelativePath(root, path), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), new FileInfo(path).Length))
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<string> directories = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(path => RelativePath(root, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new Manifest(files, directories);
    }

    static void AssertManifestEqual(Manifest expected, Manifest actual)
    {
        Ensure(expected.Files.Count == actual.Files.Count, $"File count changed: expected {expected.Files.Count}, actual {actual.Files.Count}.");
        Ensure(expected.Directories.Count == actual.Directories.Count, $"Directory count changed: expected {expected.Directories.Count}, actual {actual.Directories.Count}.");
        for (int index = 0; index < expected.Files.Count; index++)
        {
            FileEntry expectedFile = expected.Files[index];
            FileEntry actualFile = actual.Files[index];
            Ensure(string.Equals(expectedFile.Path, actualFile.Path, StringComparison.OrdinalIgnoreCase), $"File path changed: expected '{expectedFile.Path}', actual '{actualFile.Path}'.");
            Ensure(expectedFile.Length == actualFile.Length, $"File length changed for '{expectedFile.Path}'.");
            Ensure(string.Equals(expectedFile.Hash, actualFile.Hash, StringComparison.OrdinalIgnoreCase), $"File content changed for '{expectedFile.Path}'.");
        }
        for (int index = 0; index < expected.Directories.Count; index++)
            Ensure(string.Equals(expected.Directories[index], actual.Directories[index], StringComparison.OrdinalIgnoreCase), $"Directory structure changed: expected '{expected.Directories[index]}', actual '{actual.Directories[index]}'.");
    }

    static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        IDictionary<string, string?>? environment = null,
        TimeSpan? timeout = null)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (KeyValuePair<string, string?> variable in environment)
                startInfo.Environment[variable.Key] = variable.Value;
        }

        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException($"Could not start process: {fileName}");

        Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeoutSource = new(timeout ?? TestTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException exception)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception killException)
            {
                Console.Error.WriteLine($"WARNING: Could not terminate timed-out process '{fileName}': {killException.Message}");
            }
            throw new TimeoutException($"Process timed out: {fileName}", exception);
        }

        return new(process.ExitCode, await standardOutputTask, await standardErrorTask);
    }

    static string RelativePath(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    static bool PathsEqual(string left, string right) => string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    static bool IsUnderDirectory(string path, string directory)
    {
        string normalizedPath = Path.GetFullPath(path).TrimEnd('\\', '/');
        string normalizedDirectory = Path.GetFullPath(directory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    static void WriteText(string path, string content) => File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    static async Task DeleteTestDirectoryAsync(string path)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 19)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
            catch (UnauthorizedAccessException) when (attempt < 19)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        }

        Directory.Delete(path, recursive: true);
    }

    static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError);
    readonly record struct FileEntry(string Path, string Hash, long Length);
    readonly record struct Manifest(List<FileEntry> Files, List<string> Directories);
}
