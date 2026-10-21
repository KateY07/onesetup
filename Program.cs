using System.Diagnostics;
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
        bool useUi = false;
        int level = DefaultLevel;
        bool levelSpecified = false;
        for (int index = 1; index < args.Length; index++)
        {
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

            await Create7zArchiveAsync(sevenZipPath, sourceDirectory, archivePath, level);
            string shellPath = Path.Combine(temporaryDirectory, UiFileName);
            if (uiPath is null)
                await WriteEmbeddedResourceAsync("OneSetup.ui.exe", shellPath);
            else
                File.Copy(uiPath, shellPath);
            if (hasInstallScript)
                await Create7zArchiveAsync(sevenZipPath, temporaryDirectory, archivePath, level, UiFileName);

            await WriteSfxConfigAsync(configPath, hasInstallScript, useUi);
            await WriteEmbeddedResourceAsync("OneSetup.7zSD.sfx", sfxModulePath);
            string innerPath = Path.Combine(temporaryDirectory, "inner.exe");
            await ConcatenateAsync(innerPath, sfxModulePath, configPath, archivePath);
            await AssembleInstallerAsync(temporaryOutput, shellPath, innerPath);

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

    static async Task WriteSfxConfigAsync(string configPath, bool hasInstallScript, bool useUi)
    {
        StringBuilder config = new();
        config.AppendLine(";!@Install@!UTF-8!");
        config.AppendLine("Title=\"OneSetup\"");
        config.AppendLine("Progress=\"yes\"");
        if (hasInstallScript)
        {
            // The shell runs install.bat (with its window, or in a plain console with --plain) and records its exit code.
            config.AppendLine("ExecuteFile=\"onesetup-ui.exe\"");
            config.AppendLine(useUi ? "ExecuteParameters=\"install.bat\"" : "ExecuteParameters=\"--plain install.bat\"");
        }
        config.AppendLine(";!@InstallEnd@!");
        await File.WriteAllTextAsync(configPath, config.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    // setup.exe = [onesetup-ui.exe in stub mode][inner 7zSD installer][8-byte offset + "ONESETUP"].
    // 7zSD always exits 0, so the stub runs the inner installer, reads install.bat's exit code from the file named by
    // ONESETUP_RESULT and returns it as the exit code of setup.exe.
    static async Task AssembleInstallerAsync(string outputPath, string shellPath, string innerPath)
    {
        long offset = new FileInfo(shellPath).Length;
        await ConcatenateAsync(outputPath, shellPath, innerPath);
        await using FileStream output = new(outputPath, FileMode.Append, FileAccess.Write);
        await output.WriteAsync(BitConverter.GetBytes(offset));
        await output.WriteAsync(Encoding.ASCII.GetBytes("ONESETUP"));
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
              onesetup <dir> [-o <output.exe>] [-l <0-9>] [--ui | --ui-exe <file>]
              onesetup -h | --help

            OPTIONS
              -o, --output <file>  output file (default: <dir-name>_setup.exe in the current directory; never overwrites)
              -l, --level <0-9>    compression: 0 store, 1 fastest, 9 smallest (default: 9)
              --ui                 run install.bat through the built-in UI shell (requires install.bat in <dir>)
              --ui-exe <file>      like --ui, with a custom onesetup-ui.exe
              -h, --help           show this help

            EXAMPLES
              onesetup app                         pack ./app into app_setup.exe
              onesetup app -o dist/setup.exe -l 5  choose output file and compression level
              onesetup app --ui -o setup.exe       installer with progress window (steps, log, questions)

            THE INSTALLER
              Running the generated .exe extracts <dir> to a temporary folder and runs install.bat there (working directory =
              the extracted folder); the folder is removed afterwards. Without install.bat it only extracts.
              setup.exe -y   runs without confirmation prompts.

            UI PROTOCOL (with --ui, printed by install.bat to stdout)
              .title <text>   .info <text>   .warn <text>   .error <text>   .progress <0-100>   .ask <question>
              .ask answers "yes" or "no" on stdin:  echo .ask Continue?  then  set /p ANS=  and test "%ANS%"=="yes".
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
