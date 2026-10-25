using System.IO.Compression;
using NextCraftLauncher.Core.Java;
using NextCraftLauncher.Core.Minecraft;

namespace NextCraftLauncher.Core.Launch;

public sealed record LaunchOptions(
    string PlayerName,
    int MemoryMb,
    bool IsolateGameDirectory,
    int Width = 854,
    int Height = 480);

public sealed class LaunchPlan
{
    public required string JavaPath { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string VersionId { get; init; }
}

public static class LaunchPlanBuilder
{
    public const string LauncherName = "NextCraftLauncher";
    public const string LauncherVersion = "0.1.0";

    public static LaunchPlan Build(ResolvedVersion version, JavaRuntime java, LaunchOptions options)
    {
        if (!version.IsComplete)
            throw new InvalidOperationException($"版本缺少 {version.MissingFiles.Count} 个必要文件，无法启动。");

        if (java.MajorVersion < version.RequiredJavaMajor)
            throw new InvalidOperationException($"需要 Java {version.RequiredJavaMajor}，当前选择的是 Java {java.MajorVersion}。");

        if (string.IsNullOrWhiteSpace(version.MainClass))
            throw new InvalidOperationException("版本 JSON 中缺少 mainClass。");

        Directory.CreateDirectory(version.NativesDirectory);
        ExtractLegacyNatives(version.NativeLibraries, version.NativesDirectory);

        var environment = LaunchEnvironment.Current;
        var username = string.IsNullOrWhiteSpace(options.PlayerName) ? "Player" : options.PlayerName.Trim();
        var uuid = OfflineAuth.CreateOfflineUuid(username);
        var gameDirectory = options.IsolateGameDirectory
            ? version.VersionDirectory
            : version.MinecraftDirectory;

        Directory.CreateDirectory(gameDirectory);

        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = username,
            ["version_name"] = version.Id,
            ["game_directory"] = gameDirectory,
            ["assets_root"] = version.AssetsDirectory,
            ["game_assets"] = Path.Combine(version.AssetsDirectory, "virtual", "legacy"),
            ["assets_index_name"] = version.AssetsIndexId,
            ["auth_uuid"] = uuid,
            ["auth_access_token"] = "0",
            ["auth_session"] = $"token:0:{uuid}",
            ["user_type"] = "msa",
            ["version_type"] = version.Merged.Type ?? "release",
            ["user_properties"] = "{}",
            ["clientid"] = string.Empty,
            ["auth_xuid"] = string.Empty,
            ["launcher_name"] = LauncherName,
            ["launcher_version"] = LauncherVersion,
            ["natives_directory"] = version.NativesDirectory,
            ["classpath"] = version.Classpath,
            ["resolution_width"] = options.Width.ToString(),
            ["resolution_height"] = options.Height.ToString(),
        };

        var arguments = new List<string>
        {
            $"-Xmx{Math.Max(512, options.MemoryMb)}M",
            "-Dfile.encoding=UTF-8",
            "-Dstdout.encoding=UTF-8",
            "-Dstderr.encoding=UTF-8",
        };

        // 新版 JSON 的 default-user-jvm 提供内存与 GC 默认值；内存由启动器控制，因此剔除 -Xms/-Xmx。
        var jvmArguments = Flatten(version.Merged.Arguments?.DefaultUserJvm, environment)
            .Where(argument => !argument.StartsWith("-Xms", StringComparison.Ordinal) &&
                               !argument.StartsWith("-Xmx", StringComparison.Ordinal))
            .Concat(Flatten(version.Merged.Arguments?.Jvm, environment))
            .Select(argument => Substitute(argument, placeholders))
            .ToList();

        var hasClasspath = jvmArguments.Any(argument => argument is "-cp" or "-classpath");
        var hasLibraryPath = jvmArguments.Any(argument =>
            argument.StartsWith("-Djava.library.path", StringComparison.Ordinal));

        arguments.AddRange(jvmArguments);
        if (!hasLibraryPath) arguments.Add($"-Djava.library.path={version.NativesDirectory}");
        if (!hasClasspath)
        {
            arguments.Add("-cp");
            arguments.Add(version.Classpath);
        }

        arguments.Add(version.MainClass);

        foreach (var argument in BuildGameArguments(version, environment, placeholders))
            arguments.Add(argument);

        return new LaunchPlan
        {
            JavaPath = java.Path,
            Arguments = arguments,
            WorkingDirectory = gameDirectory,
            VersionId = version.Id,
        };
    }

    private static IEnumerable<string> BuildGameArguments(
        ResolvedVersion version,
        LaunchEnvironment environment,
        IReadOnlyDictionary<string, string> placeholders)
    {
        if (version.Merged.Arguments is { Game.Count: > 0 } arguments)
            return Flatten(arguments.Game, environment).Select(argument => Substitute(argument, placeholders));

        // 1.12 及更早版本使用单个 minecraftArguments 字符串。
        if (!string.IsNullOrWhiteSpace(version.Merged.MinecraftArguments))
            return version.Merged.MinecraftArguments
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(argument => Substitute(argument, placeholders));

        return Array.Empty<string>();
    }

    private static IEnumerable<string> Flatten(IReadOnlyList<ArgumentEntry>? entries, LaunchEnvironment environment)
    {
        if (entries is null) yield break;

        foreach (var entry in entries)
        {
            if (!RuleEvaluator.IsAllowed(entry.Rules, environment)) continue;
            foreach (var value in entry.Values) yield return value;
        }
    }

    private static string Substitute(string argument, IReadOnlyDictionary<string, string> placeholders)
    {
        foreach (var (key, value) in placeholders)
            argument = argument.Replace("${" + key + "}", value, StringComparison.Ordinal);
        return argument;
    }

    private static void ExtractLegacyNatives(IReadOnlyList<ResolvedLibrary> nativeLibraries, string nativesDirectory)
    {
        var root = Path.GetFullPath(nativesDirectory);

        foreach (var library in nativeLibraries)
        {
            if (!File.Exists(library.Path)) continue;

            try
            {
                using var archive = ZipFile.OpenRead(library.Path);
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;

                    var destination = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: true);
                }
            }
            catch (IOException)
            {
                // natives 解压失败不影响非依赖该库的版本，启动时会由 Java 报出具体原因。
            }
            catch (InvalidDataException)
            {
            }
        }
    }
}
