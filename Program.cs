using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace OneSetup;

static class Program
{
    static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintUsage();
                return args.Length == 0 ? 2 : 0;
            }

            return await PackAsync(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"ERROR: {exception.Message}");
            if (Environment.GetEnvironmentVariable("ONESETUP_DEBUG") == "1") Console.Error.WriteLine(exception);
            return 1;
        }
    }

    static async Task<int> PackAsync(string[] args)
    {
        string sourceDirectory = Path.GetFullPath(args[0]);
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"Source directory not found: {sourceDirectory}");

        string? outputArgument = null;
        string? uiPath = null;
        string? appEntry = null;
        string? iconPath = null;
        bool useUi = false;
        int level = DefaultLevel;
        bool levelSpecified = false;
        for (int index = 1; index < args.Length; index++)
        {
            if (args[index] == "--icon")
            {
                if (++index >= args.Length || !File.Exists(args[index]))
                    throw new ArgumentException("--icon requires the path of an existing .ico file.");
                if (iconPath is not null)
                    throw new ArgumentException("Duplicate --icon.");

                iconPath = Path.GetFullPath(args[index]);
                continue;
            }

            if (args[index] == "--app")
            {
                if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    throw new ArgumentException("--app requires the path of the program to start, relative to the directory.");
                if (appEntry is not null)
                    throw new ArgumentException("Duplicate --app.");

                appEntry = args[index];
                continue;
            }

            if (args[index] == "--ui")
            {
                useUi = true;
                continue;
            }

            if (args[index] == "--ui-exe")
            {
                if (++index >= args.Length || !File.Exists(args[index]))
                    throw new ArgumentException("--ui-exe requires the path of an existing onesetup-ui.exe.");

                useUi = true;
                uiPath = Path.GetFullPath(args[index]);
                continue;
            }

            if (args[index] is "-l" or "--level")
            {
                if (levelSpecified)
                    throw new ArgumentException("Duplicate -l/--level.");
                if (++index >= args.Length || !int.TryParse(args[index], out level) || level is < 0 or > 9)
                    throw new ArgumentException("-l/--level requires an integer from 0 (store) to 9 (smallest, slowest).");

                levelSpecified = true;
                continue;
            }

            if (args[index] is "-o" or "--output")
            {
                if (++index >= args.Length)
                    throw new ArgumentException("Missing output path after -o/--output.");

                outputArgument = args[index];
                continue;
            }

            throw new ArgumentException($"Unknown argument: {args[index]}");
        }

        string directoryName = new DirectoryInfo(sourceDirectory).Name;
        if (string.IsNullOrWhiteSpace(directoryName))
            directoryName = "package";

        string outputPath = Path.GetFullPath(outputArgument ?? Path.Combine(Environment.CurrentDirectory, $"{directoryName}_setup.exe"));
        string currentExecutable = Environment.ProcessPath ?? throw new InvalidOperationException("Unable to locate the running executable.");
        if (PathsEqual(outputPath, currentExecutable))
            throw new InvalidOperationException("Output path cannot be the running onesetup executable.");

        if (IsInsideDirectory(outputPath, sourceDirectory))
            throw new InvalidOperationException("Output path cannot be inside the source directory.");

        if (File.Exists(outputPath))
            throw new IOException($"Output file already exists: {outputPath}");

        string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"onesetup-pack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        string sevenZipPath = Path.Combine(temporaryDirectory, "7z.exe");
        string sevenZipLibraryPath = Path.Combine(temporaryDirectory, "7z.dll");
        string archivePath = Path.Combine(temporaryDirectory, "payload.7z");
        string configPath = Path.Combine(temporaryDirectory, "config.txt");
        string sfxModulePath = Path.Combine(temporaryDirectory, "7zSD.sfx");
        string temporaryOutput = Path.Combine(temporaryDirectory, "package.exe");

        try
        {
            await WriteEmbeddedResourceAsync("OneSetup.7z.exe", sevenZipPath);
            await WriteEmbeddedResourceAsync("OneSetup.7z.dll", sevenZipLibraryPath);
            bool hasInstallScript = File.Exists(Path.Combine(sourceDirectory, "install.bat"));
            if (useUi && !hasInstallScript)
                throw new InvalidOperationException("--ui needs an install.bat in the source directory.");
            if (appEntry is not null)
            {
                if (useUi)
                    throw new ArgumentException("--app and --ui cannot be combined: an app package runs the program, not install.bat.");
                appEntry = appEntry.Replace('/', '\\');
                if (Path.IsPathRooted(appEntry) || appEntry.Split('\\').Contains(".."))
                    throw new ArgumentException("--app must be a path inside the directory.");
                if (!File.Exists(Path.Combine(sourceDirectory, appEntry)))
                    throw new FileNotFoundException($"--app program not found in the directory: {appEntry}");
                hasInstallScript = false;   // install.bat (if any) is just payload in app mode
            }

            await Create7zArchiveAsync(sevenZipPath, sourceDirectory, archivePath, level);
            string shellPath = Path.Combine(temporaryDirectory, UiFileName);
            if (uiPath is null)
                await WriteEmbeddedResourceAsync("OneSetup.ui.exe", shellPath);
            else
                File.Copy(uiPath, shellPath);
            if (hasInstallScript || appEntry is not null)
                await Create7zArchiveAsync(sevenZipPath, temporaryDirectory, archivePath, level, UiFileName);

            await WriteSfxConfigAsync(configPath, hasInstallScript, useUi, appEntry is not null);
            await WriteEmbeddedResourceAsync("OneSetup.7zSD.sfx", sfxModulePath);
            string innerPath = Path.Combine(temporaryDirectory, "inner.exe");
            await ConcatenateAsync(innerPath, sfxModulePath, configPath, archivePath);
            await AssembleInstallerAsync(temporaryOutput, shellPath, innerPath, appEntry is null ? null : (appEntry, Path.Combine(sourceDirectory, appEntry)), iconPath);

            string? outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            File.Move(temporaryOutput, outputPath);
            Console.WriteLine($"Created: {outputPath}");
            return 0;
        }
        finally
        {
            TryDeleteDirectory(temporaryDirectory);
        }
    }

    const int DefaultLevel = 9;

    const string UiFileName = "onesetup-ui.exe";

    // item "." packs the whole directory with progress; a single file name appends that file silently.
    static async Task Create7zArchiveAsync(string sevenZipPath, string sourceDirectory, string archivePath, int level, string item = ".")
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = sevenZipPath,
            WorkingDirectory = sourceDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("a");
        startInfo.ArgumentList.Add("-t7z");
        startInfo.ArgumentList.Add($"-mx={level}");
        startInfo.ArgumentList.Add("-bsp1");
        startInfo.ArgumentList.Add("-bso0");
        startInfo.ArgumentList.Add(archivePath);
        startInfo.ArgumentList.Add(item);

        using Process process = new() { StartInfo = startInfo };
        process.Start();
        Task<int> outputTask = ReportProgressAsync(process.StandardOutput, Console.Out, interactive: !Console.IsOutputRedirected);
        Task errorTask = CopyLinesAsync(process.StandardError, Console.Error);
        await process.WaitForExitAsync();
        await Task.WhenAll(outputTask, errorTask);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"7-Zip failed while creating the archive (exit code {process.ExitCode}).");

        // 7-Zip does not always emit a final frame; its exit code confirms completion, so close the display at 100%.
        int lastPercent = await outputTask;
        if (item == "." && lastPercent >= 0 && lastPercent < 100)
            await Console.Out.WriteLineAsync(Console.IsOutputRedirected ? "Packing 100%" : $"{(char)13}Packing 100%");
        else if (item == "." && lastPercent >= 0 && !Console.IsOutputRedirected)
            await Console.Out.WriteLineAsync();
    }

    static async Task WriteSfxConfigAsync(string configPath, bool hasInstallScript, bool useUi, bool appMode)
    {
        StringBuilder config = new();
        config.AppendLine(";!@Install@!UTF-8!");
        config.AppendLine("Title=\"OneSetup\"");
        config.AppendLine("Progress=\"yes\"");
        if (appMode)
        {
            // App package: the shell moves the extracted files into the per-user cache; setup.exe then starts the program.
            config.AppendLine("ExecuteFile=\"onesetup-ui.exe\"");
            config.AppendLine("ExecuteParameters=\"--cache-commit\"");
        }
        else if (hasInstallScript)
        {
            // The shell runs install.bat (with its window, or in a plain console with --plain) and records its exit code.
            config.AppendLine("ExecuteFile=\"onesetup-ui.exe\"");
            config.AppendLine(useUi ? "ExecuteParameters=\"install.bat\"" : "ExecuteParameters=\"--plain install.bat\"");
        }
        config.AppendLine(";!@InstallEnd@!");
        await File.WriteAllTextAsync(configPath, config.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    // setup.exe = [onesetup-ui.exe in stub mode][inner 7zSD installer][metadata text][24-byte footer].
    // Footer: u64 offset of the inner installer, u32 metadata length, u32 reserved, "ONESETU2".
    // Installer packages: 7zSD always exits 0, so the stub runs the inner installer, reads install.bat's exit code from
    // the file named by ONESETUP_RESULT and returns it as the exit code of setup.exe.
    // App packages (--app): the stub starts <entry> from a per-user cache keyed by the payload hash; the first run
    // extracts into the cache, later runs skip extraction and start the program directly.
    static async Task AssembleInstallerAsync(string outputPath, string shellPath, string innerPath, (string Entry, string FullPath)? app, string? iconPath)
    {
        string stubPath = shellPath;
        if (iconPath is not null)
        {
            // Only the outer stub (the file the user sees) gets the project's icon; the copy inside the payload is untouched.
            stubPath = shellPath + ".icon.exe";
            File.Copy(shellPath, stubPath, true);
            IconResource.Apply(stubPath, iconPath);
        }
        byte[] shell = await File.ReadAllBytesAsync(stubPath);
        StringBuilder meta = new();
        if (app is { } appInfo)
        {
            await using FileStream inner = File.OpenRead(innerPath);
            string id = Convert.ToHexString(await SHA256.HashDataAsync(inner))[..32].ToLowerInvariant();
            bool console = IsConsoleProgram(appInfo.FullPath);
            SetPeSubsystem(shell, console ? (ushort)3 : (ushort)2);   // console programs need a console stub for stdin/stdout/Ctrl+C
            meta.Append("mode=app\n").Append("id=").Append(id).Append('\n').Append("entry=").Append(appInfo.Entry).Append('\n');
        }
        else meta.Append("mode=install\n");

        byte[] metaBytes = Encoding.UTF8.GetBytes(meta.ToString());
        long offset = shell.Length;
        await using FileStream output = new(outputPath, FileMode.CreateNew, FileAccess.Write);
        await output.WriteAsync(shell);
        await using (FileStream innerStream = File.OpenRead(innerPath))
            await innerStream.CopyToAsync(output);
        await output.WriteAsync(metaBytes);
        await output.WriteAsync(BitConverter.GetBytes(offset));
        await output.WriteAsync(BitConverter.GetBytes((uint)metaBytes.Length));
        await output.WriteAsync(BitConverter.GetBytes(0u));
        await output.WriteAsync(Encoding.ASCII.GetBytes("ONESETU2"));
    }

    // PE optional-header Subsystem field (offset 68 in both PE32 and PE32+): 2 = Windows GUI, 3 = console.
    static int PeSubsystemOffset(byte[] pe)
    {
        if (pe.Length < 0x40 || pe[0] != (byte)'M' || pe[1] != (byte)'Z') return -1;
        int header = BitConverter.ToInt32(pe, 0x3C);
        if (header < 0 || header + 24 + 70 > pe.Length || BitConverter.ToUInt32(pe, header) != 0x00004550) return -1;
        return header + 24 + 68;
    }

    static void SetPeSubsystem(byte[] pe, ushort subsystem)
    {
        int offset = PeSubsystemOffset(pe);
        if (offset >= 0) BitConverter.GetBytes(subsystem).CopyTo(pe, offset);
    }

    // Scripts (.bat/.cmd) and console executables get a console; GUI executables get a GUI stub (no console flash).
    static bool IsConsoleProgram(string path)
    {
        byte[] header = new byte[4096];
        using FileStream stream = File.OpenRead(path);
        int read = stream.Read(header, 0, header.Length);
        int offset = PeSubsystemOffset(header[..read]);
        return offset < 0 || BitConverter.ToUInt16(header, offset) != 2;
    }

    static async Task WriteEmbeddedResourceAsync(string resourceName, string outputPath)
    {
        await using Stream resource = typeof(Program).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The embedded resource is missing: {resourceName}");
        await using FileStream output = new(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await resource.CopyToAsync(output);
    }

    static async Task ConcatenateAsync(string outputPath, params string[] inputPaths)
    {
        await using FileStream output = new(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        foreach (string inputPath in inputPaths)
        {
            await using FileStream input = new(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await input.CopyToAsync(output);
        }
    }

    // 7-Zip (-bsp1) rewrites one status line in place ("42% 1 + file"), separated by CR. Show only the percentage:
    // a single updating line on a terminal, or one line per 10% when output is redirected.
    static async Task<int> ReportProgressAsync(TextReader reader, TextWriter writer, bool interactive)
    {
        StringBuilder frame = new();
        char[] buffer = new char[256];
        int last = -1;
        int count;
        while ((count = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            for (int index = 0; index < count; index++)
            {
                if (buffer[index] is not ((char)13 or (char)10))
                {
                    frame.Append(buffer[index]);
                    continue;
                }

                int percent = ParsePercent(frame.ToString());
                frame.Clear();
                if (percent < 0 || percent == last || (!interactive && percent != 100 && percent / 10 == last / 10))
                    continue;

                last = percent;
                if (interactive)
                    await writer.WriteAsync($"{(char)13}Packing {percent,3}%");
                else
                    await writer.WriteLineAsync($"Packing {percent}%");
            }
        }

        return last;
    }

    static int ParsePercent(string frame)
    {
        string text = frame.TrimStart();
        int digits = 0;
        while (digits < text.Length && char.IsAsciiDigit(text[digits]))
            digits++;

        return digits is > 0 and <= 3 && digits < text.Length && text[digits] == '%' && int.Parse(text[..digits]) <= 100
            ? int.Parse(text[..digits])
            : -1;
    }

    static async Task CopyLinesAsync(TextReader reader, TextWriter writer)
    {
        while (await reader.ReadLineAsync() is { } line)
            await writer.WriteLineAsync(line);
    }

    static bool IsInsideDirectory(string path, string directory)
    {
        string fullPath = Path.GetFullPath(path);
        string fullDirectory = Path.GetFullPath(directory);
        string directoryWithSeparator = fullDirectory.EndsWith(Path.DirectorySeparatorChar) ? fullDirectory : fullDirectory + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(directoryWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    static bool PathsEqual(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"WARNING: Could not remove temporary directory '{path}': {exception.Message}");
        }
    }

    static bool IsHelp(string argument) => argument is "-h" or "--help" or "/?";

    static void PrintUsage()
    {
        Console.WriteLine("""
            onesetup - pack a directory into a single-file Windows installer

            USAGE
              onesetup <dir> [-o <output.exe>] [-l <0-9>] [--icon <file.ico>] [--ui | --ui-exe <file> | --app <program>]
              onesetup -h | --help

            OPTIONS
              -o, --output <file>  output file (default: <dir-name>_setup.exe in the current directory; never overwrites)
              -l, --level <0-9>    compression: 0 store, 1 fastest, 9 smallest (default: 9)
              --ui                 run install.bat through the built-in UI shell (requires install.bat in <dir>)
              --icon <file.ico>    icon of the generated .exe (default: the OneSetup icon); also shown by the installer window
              --ui-exe <file>      like --ui, with a custom onesetup-ui.exe
              --app <program>      single-file app instead of an installer: the first run extracts to a per-user cache,
                                   later runs start <program> (path inside <dir>) straight from the cache
              -h, --help           show this help

            EXAMPLES
              onesetup app                         pack ./app into app_setup.exe
              onesetup app -o dist/setup.exe -l 5  choose output file and compression level
              onesetup app --ui -o setup.exe       installer with progress window (steps, log, questions)
              onesetup tool --app tool.exe -l 1 -o tool_portable.exe   single-file tool, fast first extraction

            THE INSTALLER
              Running the generated .exe extracts <dir> to a temporary folder and runs install.bat there (working directory =
              the extracted folder); the folder is removed afterwards. Without install.bat it only extracts.
              setup.exe -y   runs without confirmation prompts.

            APP PACKAGES (--app)
              The generated .exe starts <program> from %LOCALAPPDATA%\OneSetup\apps\<payload-hash>. The first run extracts
              there (a lower -l such as 1 or 0 makes it faster); every later run only checks a marker file and starts the
              program, costing about one extra process start (roughly 10 ms) over running it directly. Arguments, working
              directory, console and exit code pass through; Ctrl+C reaches the program. install.bat is not run.
              ONESETUP_CACHE overrides the cache folder. Cache entries of older builds are not removed automatically.

            UI PROTOCOL (with --ui, printed by install.bat to stdout)
              .title <text>   .info <text>   .warn <text>   .error <text>   .progress <0-100>
              .ask <question> [| yes label | no label]   answers "yes" or "no" on stdin:
                  echo .ask Continue?  then  set /p ANS=  and test "%ANS%"=="yes".
              .choose <prompt>; <option 1>; <option 2>; ...   2 to 5 options, ';' or '|' as separator ('|' needs ^| in .bat);
                  the 1-based number of the chosen option arrives on stdin (0 = dismissed):  set /p PICK=
                  Keys: 1-5 pick, Enter = first option, Esc = dismiss.
              All other output is kept in the Details log. Keep install.bat ASCII and print localised text from a UTF-8
              file with "type msg.txt". stdin carries the answers, so do not use pause or choice: ONESETUP_SHELL is set to
              "ui" (or "headless") for the script, e.g.  if not defined ONESETUP_SHELL pause. Full reference: ui/README.md.

            EXIT CODES
              onesetup: 0 success, 1 error, 2 no arguments.
              Generated installer: install.bat's exit code (the 7zSD module alone always returns 0; a small stub in front
              of it collects the real code). With --ui, cancelling in the window returns 2. If the installer cannot even
              start or extract, the code is non-zero.
            """);
    }
}
