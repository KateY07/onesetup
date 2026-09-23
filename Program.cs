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
        for (int index = 1; index < args.Length; index++)
        {
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

        ToolPaths tools = Find7ZipTools(currentExecutable);
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"onesetup-pack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        string archivePath = Path.Combine(temporaryDirectory, "payload.7z");
        string configPath = Path.Combine(temporaryDirectory, "config.txt");
        string temporaryOutput = Path.Combine(temporaryDirectory, "package.exe");

        try
        {
            await Create7zArchiveAsync(tools.SevenZipPath, sourceDirectory, archivePath);
            await WriteSfxConfigAsync(configPath, File.Exists(Path.Combine(sourceDirectory, "install.bat")));
            await ConcatenateAsync(temporaryOutput, tools.SfxModulePath, configPath, archivePath);

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

    static async Task Create7zArchiveAsync(string sevenZipPath, string sourceDirectory, string archivePath)
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
        startInfo.ArgumentList.Add("-mx=9");
        startInfo.ArgumentList.Add(archivePath);
        startInfo.ArgumentList.Add(".");

        using Process process = new() { StartInfo = startInfo };
        process.Start();
        Task outputTask = CopyLinesAsync(process.StandardOutput, Console.Out);
        Task errorTask = CopyLinesAsync(process.StandardError, Console.Error);
        await process.WaitForExitAsync();
        await Task.WhenAll(outputTask, errorTask);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"7-Zip failed while creating the archive (exit code {process.ExitCode}).");
    }

    static async Task WriteSfxConfigAsync(string configPath, bool hasInstallScript)
    {
        StringBuilder config = new();
        config.AppendLine(";!@Install@!UTF-8!");
        config.AppendLine("Title=\"OneSetup\"");
        config.AppendLine("Progress=\"yes\"");
        config.AppendLine("OverwriteMode=\"1\"");
        config.AppendLine("TempMode=\"yes\"");
        if (hasInstallScript)
            config.AppendLine("RunProgram=\"cmd.exe /d /c call \\\"%%T\\\\install.bat\\\"\"");
        config.AppendLine(";!@InstallEnd@!");
        await File.WriteAllTextAsync(configPath, config.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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

    static async Task CopyLinesAsync(TextReader reader, TextWriter writer)
    {
        while (await reader.ReadLineAsync() is { } line)
            await writer.WriteLineAsync(line);
    }

    static ToolPaths Find7ZipTools(string currentExecutable)
    {
        string currentDirectory = Path.GetDirectoryName(currentExecutable) ?? Environment.CurrentDirectory;
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string[] roots =
        [
            currentDirectory,
            Path.Combine(programFiles, "7-Zip"),
            Path.Combine(programFilesX86, "7-Zip")
        ];

        string[] distinctRoots = roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string? sevenZipPath = distinctRoots
            .Select(root => Path.Combine(root, "7z.exe"))
            .FirstOrDefault(File.Exists);
        string? sfxPath = distinctRoots
            .SelectMany(root => new[] { Path.Combine(root, "sfx", "7zSD.sfx"), Path.Combine(root, "7zSD.sfx") })
            .FirstOrDefault(File.Exists);
        if (sevenZipPath is not null && sfxPath is not null)
            return new ToolPaths(sevenZipPath, sfxPath);

        throw new FileNotFoundException("7z.exe and 7zSD.sfx were not found. Install 7-Zip or place 7z.exe beside onesetup.exe.");
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
        Console.WriteLine("onesetup - 7-Zip self-extracting archive packer");
        Console.WriteLine();
        Console.WriteLine("Pack a directory:");
        Console.WriteLine("  onesetup.exe <path-to-dir> [-o <output.exe>]");
        Console.WriteLine();
        Console.WriteLine("The default output is <directory-name>_setup.exe in the current directory.");
        Console.WriteLine("The packer requires 7z.exe; the 7zSD.sfx installer module is included with the build.");
    }

    readonly record struct ToolPaths(string SevenZipPath, string SfxModulePath);
}
