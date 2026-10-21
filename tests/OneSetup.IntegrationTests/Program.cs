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
        string directReportPath = Path.Combine(testRoot, "direct-bat-report.txt");
        string sfxReportPath = Path.Combine(testRoot, "sfx-bat-report.txt");
        string extractedDirectory = Path.Combine(testRoot, "extracted-by-7z");
        Directory.CreateDirectory(runnerDirectory);
        Directory.CreateDirectory(extractedDirectory);
        CreateFixture(sourceDirectory);
        Manifest expectedManifest = CaptureManifest(sourceDirectory);

        ProcessResult directResult = await RunProcessAsync(
            "cmd.exe",
            ["/d", "/c", "call", Path.Combine(sourceDirectory, "install.bat")],
            sourceDirectory,
            new Dictionary<string, string?>
            {
                ["ONESETUP_TEST_BAT_RESULT"] = directReportPath
            },
            TestTimeout);
        Ensure(directResult.ExitCode == 0, $"Direct install.bat execution failed with exit code {directResult.ExitCode}.\nSTDOUT:\n{directResult.StandardOutput}\nSTDERR:\n{directResult.StandardError}");
        BatchReport directReport = ReadBatchReport(directReportPath);
        Ensure(PathsEqual(directReport.CurrentDirectory, sourceDirectory), $"Direct install.bat current directory was incorrect: {directReport.CurrentDirectory}");
        Ensure(PathsEqual(directReport.ScriptDirectory, sourceDirectory), $"Direct install.bat script directory was incorrect: {directReport.ScriptDirectory}");

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
        File.Delete(Path.Combine(extractedDirectory, "onesetup-ui.exe"));   // the shell that runs install.bat travels with the payload
        AssertManifestEqual(expectedManifest, CaptureManifest(extractedDirectory));

        ProcessResult collisionResult = await RunProcessAsync(
            packerPath,
            [sourceDirectory, "-o", setupPath],
            runnerDirectory,
            timeout: TestTimeout);
        Ensure(collisionResult.ExitCode != 0, "A second package operation overwrote an existing output file.");
        Ensure(collisionResult.StandardError.Contains("already exists", StringComparison.OrdinalIgnoreCase), "The output collision error was not reported.");

        await AssertLevelAndProgressAsync(packerPath, sevenZipPath, testRoot, runnerDirectory);
        await AssertUiPackagingAsync(packerPath, sevenZipPath, sourceDirectory, runnerDirectory);
        await AssertFailureExitCodeAsync(packerPath, runnerDirectory, testRoot);
        await AssertAppModeAsync(packerPath, runnerDirectory, testRoot);
        await AssertChoiceProtocolAsync(packerPath, runnerDirectory, testRoot);
        await AssertLicenseProtocolAsync(packerPath, runnerDirectory, testRoot);
        await AssertIconAsync(packerPath, runnerDirectory, testRoot);
        await AssertLaunchedProgramSurvivesAsync(packerPath, runnerDirectory, testRoot);

        ProcessResult sfxResult = await RunProcessAsync(
            setupPath,
            ["-y"],
            runnerDirectory,
            new Dictionary<string, string?>
            {
                ["ONESETUP_TEST_BAT_RESULT"] = sfxReportPath
            },
            TestTimeout);
        Ensure(sfxResult.ExitCode == 0, $"SFX execution failed with exit code {sfxResult.ExitCode}.\nSTDOUT:\n{sfxResult.StandardOutput}\nSTDERR:\n{sfxResult.StandardError}");
        Ensure(File.Exists(sfxReportPath), "install.bat was not executed by the SFX.");
        BatchReport sfxReport = ReadBatchReport(sfxReportPath);
        Ensure(PathsEqual(sfxReport.CurrentDirectory, sfxReport.ScriptDirectory), $"SFX install.bat current directory did not match its script directory. Current='{sfxReport.CurrentDirectory}', ScriptRoot='{sfxReport.ScriptDirectory}'.");
        Ensure(IsUnderDirectory(sfxReport.CurrentDirectory, Path.GetTempPath()), $"SFX install.bat did not run from a temporary extraction directory: {sfxReport.CurrentDirectory}");
        AssertEnvironmentEqual(directReport.Environment, sfxReport.Environment);
        AssertManifestEqual(expectedManifest, CaptureManifest(sourceDirectory));
    }

    static void CreateFixture(string sourceDirectory)
    {
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "nested"));
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "empty-directory"));
        WriteText(Path.Combine(sourceDirectory, "payload.txt"), "payload\r\n");
        WriteText(Path.Combine(sourceDirectory, "nested", "config.json"), "{\"enabled\":true}\r\n");
        WriteText(Path.Combine(sourceDirectory, "unicode-测试.txt"), "unicode payload\r\n");
        WriteText(Path.Combine(sourceDirectory, "install.bat"), "@echo off\r\nsetlocal\r\n> \"%ONESETUP_TEST_BAT_RESULT%\" echo install-bat-ran\r\n>> \"%ONESETUP_TEST_BAT_RESULT%\" echo current-directory=%CD%\r\n>> \"%ONESETUP_TEST_BAT_RESULT%\" echo script-directory=%~dp0\r\n>> \"%ONESETUP_TEST_BAT_RESULT%\" echo environment-start\r\n>> \"%ONESETUP_TEST_BAT_RESULT%\" set\r\nendlocal\r\nexit /b 0\r\n");
    }

    static void AssertSfxConfiguration(string setupPath)
    {
        string packageText = Encoding.UTF8.GetString(File.ReadAllBytes(setupPath));
        Ensure(packageText.Contains("ExecuteFile=\"onesetup-ui.exe\"", StringComparison.Ordinal) && packageText.Contains("ExecuteParameters=\"--plain install.bat\"", StringComparison.Ordinal), "The generated SFX does not run install.bat through the shell.");
        Ensure(!packageText.Contains("RunProgram=\"cmd.exe", StringComparison.Ordinal), "The generated SFX still uses the broken RunProgram cmd.exe path.");
    }

    static BatchReport ReadBatchReport(string reportPath)
    {
        string[] lines = File.ReadAllLines(reportPath);
        Ensure(lines.Length >= 3, "install.bat did not write its complete environment report.");
        Ensure(lines[0].Trim() == "install-bat-ran", "install.bat marker content is incorrect.");
        string currentDirectory = ValueAfter(lines, "current-directory=");
        string scriptDirectory = ValueAfter(lines, "script-directory=").TrimEnd('\\', '/');
        int environmentStart = Array.IndexOf(lines, "environment-start");
        Ensure(environmentStart >= 0, "install.bat did not write its environment report.");
        Dictionary<string, string> environment = lines
            .Skip(environmentStart + 1)
            .Where(line => line.Contains('='))
            .Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => NormalizeEnvironmentValue(parts[0], parts[1]), StringComparer.OrdinalIgnoreCase);
        return new BatchReport(currentDirectory, scriptDirectory, environment);
    }

    static void AssertEnvironmentEqual(Dictionary<string, string> expected, Dictionary<string, string> actual)
    {
        foreach ((string name, string value) in expected)
        {
            Ensure(actual.TryGetValue(name, out string? actualValue), $"Environment variable was missing: {name}");
            Ensure(string.Equals(value, actualValue, StringComparison.Ordinal), $"Environment variable changed: {name}");
        }
        foreach (string name in actual.Keys.Where(name => !name.Equals("ONESETUP_RESULT", StringComparison.OrdinalIgnoreCase)))   // set by the setup.exe stub
            Ensure(expected.ContainsKey(name), $"Unexpected environment variable: {name}");
    }

    static string NormalizeEnvironmentValue(string name, string value) => name.Equals("CMDCMDLINE", StringComparison.OrdinalIgnoreCase) || name.Equals("ONESETUP_TEST_BAT_RESULT", StringComparison.OrdinalIgnoreCase) ? "<normalized>" : value;

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

    // A program started by install.bat ("start app.exe") must outlive the installer, and the UI shell must not wait for it.
    static async Task AssertLaunchedProgramSurvivesAsync(string packerPath, string runnerDirectory, string testRoot)
    {
        string launcher = Path.Combine(testRoot, "launcher");
        Directory.CreateDirectory(launcher);
        string marker = Path.Combine(testRoot, "launched.txt");
        await File.WriteAllTextAsync(Path.Combine(launcher, "install.bat"),
            "@echo off" + Environment.NewLine +
            "echo .info Starting the program" + Environment.NewLine +
            "start \"\" cmd /c \"ping -n 7 127.0.0.1 >nul & echo launched> \"%ONESETUP_TEST_MARKER%\"\"" + Environment.NewLine +
            "exit /b 0" + Environment.NewLine);
        string setup = Path.Combine(runnerDirectory, "launcher_setup.exe");
        ProcessResult pack = await RunProcessAsync(packerPath, [launcher, "-o", setup, "-l", "1", "--ui"], runnerDirectory, timeout: TestTimeout);
        Ensure(pack.ExitCode == 0, $"Packing failed. {pack.StandardError}");
        Stopwatch watch = Stopwatch.StartNew();
        ProcessResult run = await RunProcessAsync(setup, ["-y"], runnerDirectory,
            new Dictionary<string, string?> { ["ONESETUP_UI"] = "0", ["ONESETUP_TEST_MARKER"] = marker }, TestTimeout);
        watch.Stop();
        Ensure(run.ExitCode == 0, $"Installer failed with exit code {run.ExitCode}.");
        Ensure(watch.Elapsed < TimeSpan.FromSeconds(4.5), $"The installer waited for the program it launched ({watch.Elapsed.TotalSeconds:0.0}s; the program runs 6s).");
        for (int attempt = 0; attempt < 120 && !File.Exists(marker); attempt++)
            await Task.Delay(100);
        Ensure(File.Exists(marker), "The program started by install.bat did not survive the installer.");
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr LoadLibraryExW(string fileName, IntPtr reserved, uint flags);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern IntPtr FindResourceW(IntPtr module, IntPtr name, IntPtr type);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern IntPtr LockResource(IntPtr resource);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool FreeLibrary(IntPtr module);

    // Resource bytes (type, id) of an executable, or null when it has none.
    static byte[]? ReadResource(string path, int type, int id)
    {
        IntPtr module = LoadLibraryExW(path, IntPtr.Zero, 0x2);   // LOAD_LIBRARY_AS_DATAFILE
        Ensure(module != IntPtr.Zero, $"Cannot load {path} to inspect its resources.");
        try
        {
            IntPtr resource = FindResourceW(module, id, type);
            if (resource == IntPtr.Zero) return null;
            byte[] bytes = new byte[SizeofResource(module, resource)];
            System.Runtime.InteropServices.Marshal.Copy(LockResource(LoadResource(module, resource)), bytes, 0, bytes.Length);
            return bytes;
        }
        finally { FreeLibrary(module); }
    }

    // Every setup.exe has the OneSetup icon; --icon replaces it with the given .ico (images 101.., group 1).
    static async Task AssertIconAsync(string packerPath, string runnerDirectory, string testRoot)
    {
        string source = Path.Combine(testRoot, "icon-source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "install.bat"), "@echo off\r\nexit /b 0\r\n");

        string defaultSetup = Path.Combine(runnerDirectory, "icon_default.exe");
        ProcessResult plain = await RunProcessAsync(packerPath, [source, "-o", defaultSetup, "-l", "1"], runnerDirectory, timeout: TestTimeout);
        Ensure(plain.ExitCode == 0, $"Packing failed. {plain.StandardError}");
        byte[]? defaultGroup = ReadResource(defaultSetup, 14, 1);
        Ensure(defaultGroup is { Length: > 6 }, "A setup.exe made without --icon has no icon.");

        byte[] imageA = Enumerable.Range(0, 120).Select(i => (byte)(i * 3)).ToArray(), imageB = Enumerable.Range(0, 200).Select(i => (byte)(i * 7)).ToArray();
        using (MemoryStream ico = new())
        using (BinaryWriter writer = new(ico))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)2);
            int offset = 6 + 32;
            foreach ((byte size, byte[] image) in new[] { ((byte)16, imageA), ((byte)32, imageB) })
            {
                writer.Write(size); writer.Write(size); writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((ushort)1); writer.Write((ushort)32); writer.Write((uint)image.Length); writer.Write((uint)offset);
                offset += image.Length;
            }
            writer.Write(imageA); writer.Write(imageB);
            await File.WriteAllBytesAsync(Path.Combine(testRoot, "test.ico"), ico.ToArray());
        }

        string customSetup = Path.Combine(runnerDirectory, "icon_custom.exe");
        ProcessResult custom = await RunProcessAsync(packerPath, [source, "-o", customSetup, "-l", "1", "--icon", Path.Combine(testRoot, "test.ico")], runnerDirectory, timeout: TestTimeout);
        Ensure(custom.ExitCode == 0, $"Packing with --icon failed. {custom.StandardError}");
        byte[]? group = ReadResource(customSetup, 14, 1);
        Ensure(group is { Length: 6 + 14 * 2 } && BitConverter.ToUInt16(group, 4) == 2, "The custom icon group is wrong.");
        Ensure(ReadResource(customSetup, 3, 101)!.SequenceEqual(imageA) && ReadResource(customSetup, 3, 102)!.SequenceEqual(imageB), "The custom icon images are wrong.");
        Ensure(ReadResource(customSetup, 3, 1) is null, "The previous icon images were not removed.");

        string notIcon = Path.Combine(testRoot, "not.ico");
        await File.WriteAllTextAsync(notIcon, "not an icon");
        ProcessResult rejected = await RunProcessAsync(packerPath, [source, "-o", Path.Combine(runnerDirectory, "never_icon.exe"), "--icon", notIcon], runnerDirectory, timeout: TestTimeout);
        Ensure(rejected.ExitCode != 0 && !File.Exists(Path.Combine(runnerDirectory, "never_icon.exe")), "A file that is not an .ico was accepted by --icon.");
    }

    // .choose / .ask protocol: the answer reaches install.bat on stdin (headless shell answers from ONESETUP_CHOICE / ONESETUP_ASK).
    static async Task AssertChoiceProtocolAsync(string packerPath, string runnerDirectory, string testRoot)
    {
        string source = Path.Combine(testRoot, "choice-source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "install.bat"),
            "@echo off\r\n" +
            "echo .choose Which flavour?; Standard; Extra crunchy; Minimal\r\nset \"PICK=\"\r\nset /p PICK=\r\n>\"%CHOICE_OUT%\" echo picked=%PICK%\r\n" +
            "echo .choose Pipe separated ^| Red ^| Green\r\nset \"PIPE=\"\r\nset /p PIPE=\r\n>>\"%CHOICE_OUT%\" echo pipe=%PIPE%\r\n" +
            "echo .ask Really? ^| Absolutely ^| Not now\r\nset \"ANS=\"\r\nset /p ANS=\r\n>>\"%CHOICE_OUT%\" echo ask=%ANS%\r\n" +
            "echo .choose one option only\r\nset \"BAD=\"\r\nset /p BAD=\r\n>>\"%CHOICE_OUT%\" echo bad=%BAD%\r\n" +
            "exit /b 0\r\n");
        string setup = Path.Combine(runnerDirectory, "choice_setup.exe");
        ProcessResult pack = await RunProcessAsync(packerPath, [source, "-o", setup, "-l", "1", "--ui"], runnerDirectory, timeout: TestTimeout);
        Ensure(pack.ExitCode == 0, $"Packing failed. {pack.StandardError}");

        async Task<string> Run(string choice, string ask)
        {
            string output = Path.Combine(testRoot, $"choice-{choice}-{ask}.txt");
            ProcessResult run = await RunProcessAsync(setup, ["-y"], runnerDirectory,
                new Dictionary<string, string?> { ["ONESETUP_UI"] = "0", ["ONESETUP_CHOICE"] = choice, ["ONESETUP_ASK"] = ask, ["CHOICE_OUT"] = output }, TestTimeout);
            Ensure(run.ExitCode == 0 && File.Exists(output), $"Choice run failed ({run.ExitCode}). {run.StandardError}");
            return string.Join(' ', File.ReadAllLines(output).Select(line => line.Trim()));
        }

        Ensure(await Run("2", "yes") == "picked=2 pipe=2 ask=yes bad=0", "Picking option 2 did not reach install.bat (semicolon and pipe separators, labelled ask, too few options).");
        Ensure(await Run("1", "no") == "picked=1 pipe=1 ask=no bad=0", "The first option or the no answer did not reach install.bat.");
        Ensure(await Run("0", "yes") == "picked=0 pipe=0 ask=yes bad=0", "Dismissing a choice did not answer 0.");
        Ensure(await Run("9", "yes") == "picked=0 pipe=0 ask=yes bad=0", "An out-of-range choice did not answer 0.");
    }

    // .license answers yes/no on stdin (headless: ONESETUP_ASK), .background resolves next to the script.
    static async Task AssertLicenseProtocolAsync(string packerPath, string runnerDirectory, string testRoot)
    {
        string source = Path.Combine(testRoot, "license-source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "license.txt"), "You may use this software.\r\n");
        await File.WriteAllTextAsync(Path.Combine(source, "install.bat"),
            "@echo off\r\n" +
            "echo .background missing.png\r\n" +
            "echo .license license.txt\r\n" +
            "set \"OK=\"\r\n" +
            "set /p OK=\r\n" +
            ">\"%LICENSE_OUT%\" echo %OK%\r\n" +
            "echo .license nothere.txt\r\n" +
            "set \"NO=\"\r\n" +
            "set /p NO=\r\n" +
            ">>\"%LICENSE_OUT%\" echo %NO%\r\n" +
            "if /i not \"%OK%\"==\"yes\" exit /b 7\r\n" +
            "exit /b 0\r\n");
        string setup = Path.Combine(runnerDirectory, "license_setup.exe");
        ProcessResult pack = await RunProcessAsync(packerPath, [source, "-o", setup, "-l", "1", "--ui"], runnerDirectory, timeout: TestTimeout);
        Ensure(pack.ExitCode == 0, $"Packing failed. {pack.StandardError}");
        foreach ((string ask, int code, string lines) in new[] { ("yes", 0, "yes no"), ("no", 7, "no no") })
        {
            string output = Path.Combine(testRoot, $"license-{ask}.txt");
            ProcessResult run = await RunProcessAsync(setup, ["-y"], runnerDirectory,
                new Dictionary<string, string?> { ["ONESETUP_UI"] = "0", ["ONESETUP_ASK"] = ask, ["LICENSE_OUT"] = output }, TestTimeout);
            Ensure(run.ExitCode == code && File.Exists(output), $"License run '{ask}' returned {run.ExitCode}, expected {code}. {run.StandardError}");
            Ensure(string.Join(' ', File.ReadAllLines(output).Select(line => line.Trim())) == lines, $"License answers were wrong for '{ask}'.");
        }
    }

    // --app: first run extracts into a per-user cache keyed by the payload hash, later runs start the program directly.
    static async Task AssertAppModeAsync(string packerPath, string runnerDirectory, string testRoot)
    {
        string source = Path.Combine(testRoot, "app-source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "hello.bat"),
            "@echo off\r\necho cwd=%CD%\r\necho args=%*\r\necho run>> \"%~dp0runs.txt\"\r\nexit /b %HELLO_EXIT%\r\n");
        await File.WriteAllTextAsync(Path.Combine(source, "data.txt"), "payload");
        string setup = Path.Combine(runnerDirectory, "app_setup.exe");
        ProcessResult pack = await RunProcessAsync(packerPath, [source, "-o", setup, "-l", "1", "--app", "hello.bat"], runnerDirectory, timeout: TestTimeout);
        Ensure(pack.ExitCode == 0, $"Packing with --app failed. {pack.StandardError}");

        string cache = Path.Combine(testRoot, "app-cache"), work = Path.Combine(testRoot, "app-work");
        Directory.CreateDirectory(work);
        Dictionary<string, string?> environment = new() { ["ONESETUP_CACHE"] = cache, ["HELLO_EXIT"] = "0" };

        ProcessResult cold = await RunProcessAsync(setup, ["one", "two words"], work, environment, TestTimeout);
        Ensure(cold.ExitCode == 0, $"Cold run failed ({cold.ExitCode}). {cold.StandardError}");
        Ensure(cold.StandardOutput.Contains($"cwd={work}", StringComparison.OrdinalIgnoreCase), $"The program did not run in the caller's directory. {cold.StandardOutput}");
        Ensure(cold.StandardOutput.Contains("args=one \"two words\"", StringComparison.Ordinal), $"Arguments were not passed through. {cold.StandardOutput}");
        string[] entries = Directory.GetDirectories(cache);
        Ensure(entries.Length == 1 && File.Exists(Path.Combine(entries[0], ".ready")), "The first run did not leave exactly one finished cache entry.");
        DateTime readyStamp = File.GetLastWriteTimeUtc(Path.Combine(entries[0], ".ready"));

        ProcessResult warm = await RunProcessAsync(setup, ["three"], work, environment, TestTimeout);
        Ensure(warm.ExitCode == 0 && warm.StandardOutput.Contains("args=three", StringComparison.Ordinal), $"Warm run failed ({warm.ExitCode}). {warm.StandardError}");
        Ensure(Directory.GetDirectories(cache).Length == 1 && File.GetLastWriteTimeUtc(Path.Combine(entries[0], ".ready")) == readyStamp, "The warm run extracted again.");
        Ensure(File.ReadAllLines(Path.Combine(entries[0], "runs.txt")).Length == 2, "The program did not run twice from the same cache entry.");

        environment["HELLO_EXIT"] = "7";
        ProcessResult failing = await RunProcessAsync(setup, [], work, environment, TestTimeout);
        Ensure(failing.ExitCode == 7, $"The program's exit code was not returned (got {failing.ExitCode}).");

        // Several first runs at once: all succeed, exactly one cache entry, no staging folders left behind.
        string raceCache = Path.Combine(testRoot, "app-race-cache");
        Dictionary<string, string?> raceEnvironment = new() { ["ONESETUP_CACHE"] = raceCache, ["HELLO_EXIT"] = "0" };
        ProcessResult[] race = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => RunProcessAsync(setup, [], work, raceEnvironment, TestTimeout)));
        Ensure(race.All(result => result.ExitCode == 0), $"A concurrent first run failed: {string.Join(',', race.Select(result => result.ExitCode))}");
        string[] raceEntries = Directory.GetDirectories(raceCache);
        Ensure(raceEntries.Length == 1 && !raceEntries[0].Contains(".tmp-", StringComparison.Ordinal), $"Concurrent first runs left {raceEntries.Length} entries.");

        ProcessResult missing = await RunProcessAsync(packerPath, [source, "-o", Path.Combine(runnerDirectory, "never_app.exe"), "--app", "absent.exe"], runnerDirectory, timeout: TestTimeout);
        Ensure(missing.ExitCode != 0 && !File.Exists(Path.Combine(runnerDirectory, "never_app.exe")), "--app accepted a program that is not in the directory.");
    }

    // install.bat's exit code must reach the caller of setup.exe (the 7zSD module alone always returns 0).
    static async Task AssertFailureExitCodeAsync(string packerPath, string runnerDirectory, string testRoot)
    {
        string failing = Path.Combine(testRoot, "failing");
        Directory.CreateDirectory(failing);
        string marker = Path.Combine(testRoot, "failing-ran.txt");
        await File.WriteAllTextAsync(Path.Combine(failing, "install.bat"), "@echo off" + Environment.NewLine + "echo ran> \"%ONESETUP_TEST_MARKER%\"" + Environment.NewLine + "exit /b 3" + Environment.NewLine);
        string setup = Path.Combine(runnerDirectory, "failing_setup.exe");
        ProcessResult pack = await RunProcessAsync(packerPath, [failing, "-o", setup, "-l", "1"], runnerDirectory, timeout: TestTimeout);
        Ensure(pack.ExitCode == 0, $"Packing failed. {pack.StandardError}");
        ProcessResult run = await RunProcessAsync(setup, ["-y"], runnerDirectory, new Dictionary<string, string?> { ["ONESETUP_TEST_MARKER"] = marker }, TestTimeout);
        Ensure(File.Exists(marker), "The failing install.bat did not run.");
        Ensure(run.ExitCode == 3, $"setup.exe did not return install.bat's exit code 3 (got {run.ExitCode}).");

        string uiSetup = Path.Combine(runnerDirectory, "failing_ui_setup.exe");
        ProcessResult uiPack = await RunProcessAsync(packerPath, [failing, "-o", uiSetup, "-l", "1", "--ui"], runnerDirectory, timeout: TestTimeout);
        Ensure(uiPack.ExitCode == 0, $"Packing with --ui failed. {uiPack.StandardError}");
        ProcessResult uiRun = await RunProcessAsync(uiSetup, ["-y"], runnerDirectory, new Dictionary<string, string?> { ["ONESETUP_TEST_MARKER"] = marker, ["ONESETUP_UI"] = "0" }, TestTimeout);
        Ensure(uiRun.ExitCode == 3, $"The --ui setup.exe did not return install.bat's exit code 3 (got {uiRun.ExitCode}).");
    }

    static async Task AssertUiPackagingAsync(string packerPath, string sevenZipPath, string sourceDirectory, string runnerDirectory)
    {
        string uiSetup = Path.Combine(runnerDirectory, "ui_setup.exe");
        ProcessResult pack = await RunProcessAsync(packerPath, [sourceDirectory, "-o", uiSetup, "-l", "1", "--ui"], runnerDirectory, timeout: TestTimeout);
        Ensure(pack.ExitCode == 0, $"Packing with --ui failed. {pack.StandardError}");
        ProcessResult list = await RunProcessAsync(sevenZipPath, ["l", uiSetup], runnerDirectory, timeout: TestTimeout);
        Ensure(list.StandardOutput.Contains("onesetup-ui.exe", StringComparison.Ordinal) && list.StandardOutput.Contains("install.bat", StringComparison.Ordinal), "The UI shell or install.bat is missing from the --ui package.");
        string packageText = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(uiSetup));
        Ensure(packageText.Contains("ExecuteFile=\"onesetup-ui.exe\"", StringComparison.Ordinal) && packageText.Contains("ExecuteParameters=\"install.bat\"", StringComparison.Ordinal), "The --ui package does not start the UI shell with install.bat.");
        string emptyDirectory = Path.Combine(runnerDirectory, "no-script");
        Directory.CreateDirectory(emptyDirectory);
        ProcessResult rejected = await RunProcessAsync(packerPath, [emptyDirectory, "-o", Path.Combine(runnerDirectory, "never_ui.exe"), "--ui"], runnerDirectory, timeout: TestTimeout);
        Ensure(rejected.ExitCode != 0, "--ui was accepted without an install.bat.");
    }

    static async Task AssertLevelAndProgressAsync(string packerPath, string sevenZipPath, string testRoot, string runnerDirectory)
    {
        string bulkDirectory = Path.Combine(testRoot, "bulk");
        Directory.CreateDirectory(bulkDirectory);
        byte[] repetitive = new byte[8 * 1024 * 1024];
        for (int index = 0; index < repetitive.Length; index++)
            repetitive[index] = (byte)(index % 251);
        await File.WriteAllBytesAsync(Path.Combine(bulkDirectory, "repetitive.bin"), repetitive);
        byte[] noise = new byte[24 * 1024 * 1024];
        new Random(7).NextBytes(noise);
        await File.WriteAllBytesAsync(Path.Combine(bulkDirectory, "noise.bin"), noise);

        string storedPath = Path.Combine(runnerDirectory, "stored_setup.exe");
        string smallestPath = Path.Combine(runnerDirectory, "smallest_setup.exe");
        ProcessResult stored = await RunProcessAsync(packerPath, [bulkDirectory, "-o", storedPath, "-l", "0"], runnerDirectory, timeout: TestTimeout);
        ProcessResult smallest = await RunProcessAsync(packerPath, [bulkDirectory, "--output", smallestPath, "--level", "9"], runnerDirectory, timeout: TestTimeout);
        Ensure(stored.ExitCode == 0 && smallest.ExitCode == 0, $"Level packing failed. {stored.StandardError} {smallest.StandardError}");
        Ensure(new FileInfo(storedPath).Length > new FileInfo(smallestPath).Length + 4 * 1024 * 1024, "-l 0 did not store while -l 9 compressed.");
        Ensure(smallest.StandardOutput.Contains("Packing 100%", StringComparison.Ordinal), $"Packing progress was not reported. STDOUT: {smallest.StandardOutput}");
        Ensure(!smallest.StandardOutput.Contains("Packing 5%", StringComparison.Ordinal), "Redirected progress should report one line per 10%.");

        foreach (string[] invalid in new[] { new[] { "-l", "10" }, new[] { "-l", "x" }, new[] { "-l" }, new[] { "-l", "1", "--level", "2" } })
        {
            ProcessResult rejected = await RunProcessAsync(packerPath, [bulkDirectory, "-o", Path.Combine(runnerDirectory, "never.exe"), .. invalid], runnerDirectory, timeout: TestTimeout);
            Ensure(rejected.ExitCode != 0 && !File.Exists(Path.Combine(runnerDirectory, "never.exe")), $"Invalid level was accepted: {string.Join(' ', invalid)}");
        }
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
    readonly record struct BatchReport(string CurrentDirectory, string ScriptDirectory, Dictionary<string, string> Environment);
}
