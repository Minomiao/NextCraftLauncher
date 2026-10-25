using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using NextCraftLauncher.Core.Logging;
using NextCraftLauncher.Core.Settings;
using NextCraftLauncher.Download.Minecraft;

namespace NextCraftLauncher.Download.Loaders;

public sealed class LoaderInstallOptions
{
    public DownloadMirror Mirror { get; init; } = DownloadMirror.Automatic;

    /// <summary>运行 Forge / NeoForge 安装器所需的 Java（Fabric 不需要）。</summary>
    public string? JavaPath { get; init; }

    /// <summary>安装后的版本目录名称；为空时沿用安装器 / profile 的默认 id。</summary>
    public string? VersionName { get; init; }

    public IProgress<LoaderInstallProgress>? Progress { get; init; }

    /// <summary>安装器实时输出的日志行（仅 Forge / NeoForge）。</summary>
    public IProgress<string>? Log { get; init; }

    public DownloadClient? Client { get; init; }
}

public sealed record LoaderInstallProgress(
    string Text,
    double? Percent,
    MinecraftDownloadStage Stage);

public sealed record LoaderInstallResult(string VersionId, string VersionDirectory);

/// <summary>
/// 安装模组加载器：
/// <list type="bullet">
/// <item>Fabric：下载官方 profile JSON，再按其中的 maven 坐标补齐库文件；</item>
/// <item>Forge / NeoForge：下载官方 installer，调用 <c>--installClient</c> 完成安装。</item>
/// </list>
/// 原版版本需要事先安装（由调用方保证，Fabric 的 inheritsFrom 依赖它）。
/// </summary>
public static class LoaderInstaller
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static Task<LoaderInstallResult> InstallAsync(
        string minecraftDirectory,
        string minecraftVersion,
        string loader,
        string loaderVersion,
        LoaderInstallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new LoaderInstallOptions();

        return loader.ToLowerInvariant() switch
        {
            "fabric" => InstallFabricAsync(
                minecraftDirectory, minecraftVersion, loaderVersion, options, cancellationToken),
            "forge" or "neoforge" => InstallWithInstallerAsync(
                minecraftDirectory, minecraftVersion, loader.ToLowerInvariant(), loaderVersion,
                options, cancellationToken),
            _ => throw new ArgumentException($"不支持的加载器：{loader}", nameof(loader)),
        };
    }

    // ---------------------------------------------------------------- Fabric

    private static async Task<LoaderInstallResult> InstallFabricAsync(
        string minecraftDirectory,
        string minecraftVersion,
        string loaderVersion,
        LoaderInstallOptions options,
        CancellationToken cancellationToken)
    {
        var client = options.Client ?? DownloadClient.Shared;

        options.Progress?.Report(new LoaderInstallProgress(
            "正在获取 Fabric 安装信息…", 0, MinecraftDownloadStage.Preparing));

        var profileUrl =
            $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(minecraftVersion)}/" +
            $"{Uri.EscapeDataString(loaderVersion)}/profile/json";

        var profileBytes = await client.DownloadBytesAutoAsync(
            DownloadUrlResolver.Build(profileUrl, options.Mirror),
            options.Mirror,
            cancellationToken: cancellationToken);

        var profile = JsonSerializer.Deserialize<FabricProfile>(profileBytes, JsonOptions)
                      ?? throw new InvalidDataException("无法解析 Fabric 安装信息。");

        var versionId = string.IsNullOrWhiteSpace(profile.Id)
            ? $"fabric-loader-{loaderVersion}-{minecraftVersion}"
            : profile.Id!;

        var versionDirectory = Path.Combine(minecraftDirectory, "versions", versionId);
        Directory.CreateDirectory(versionDirectory);

        var libraries = (profile.Libraries ?? new List<FabricLibrary>())
            .Where(library => !string.IsNullOrWhiteSpace(library.Name))
            .ToList();

        LauncherLog.Info($"Fabric profile {versionId}，需要 {libraries.Count} 个库文件");

        var completed = 0;
        var total = Math.Max(libraries.Count, 1);
        var gate = new SemaphoreSlim(8);

        var tasks = libraries.Select(async library =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                await DownloadLibraryAsync(
                    client, minecraftDirectory, library, options.Mirror, cancellationToken);

                var done = Interlocked.Increment(ref completed);
                options.Progress?.Report(new LoaderInstallProgress(
                    $"正在下载 Fabric 库文件 {done}/{libraries.Count}…",
                    Math.Min(99, (double)done / total * 100),
                    MinecraftDownloadStage.Files));
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);

        options.Progress?.Report(new LoaderInstallProgress(
            "正在写入 Fabric 版本配置…", 99, MinecraftDownloadStage.Finalizing));

        var jsonPath = Path.Combine(versionDirectory, versionId + ".json");
        await File.WriteAllBytesAsync(jsonPath, profileBytes, cancellationToken);

        var finalId = VersionNaming.RenameInstalledVersion(
            minecraftDirectory, versionId, options.VersionName);
        var finalDirectory = Path.Combine(minecraftDirectory, "versions", finalId);

        options.Progress?.Report(new LoaderInstallProgress(
            "Fabric 安装完成", 100, MinecraftDownloadStage.Finalizing));

        return new LoaderInstallResult(finalId, finalDirectory);
    }

    private static async Task DownloadLibraryAsync(
        DownloadClient client,
        string minecraftDirectory,
        FabricLibrary library,
        DownloadMirror mirror,
        CancellationToken cancellationToken)
    {
        var relativePath = DeriveMavenPath(library.Name);
        if (relativePath is null) return;

        var baseUrl = string.IsNullOrWhiteSpace(library.Url)
            ? "https://libraries.minecraft.net/"
            : library.Url!;

        var url = baseUrl.TrimEnd('/') + "/" + relativePath;
        var destination = Path.Combine(
            minecraftDirectory, "libraries", relativePath.Replace('/', Path.DirectorySeparatorChar));

        await client.DownloadFileAsync(
            DownloadUrlResolver.Build(url, mirror),
            destination,
            string.IsNullOrWhiteSpace(library.Sha1) ? null : library.Sha1,
            library.Size > 0 ? library.Size : null,
            cancellationToken: cancellationToken);
    }

    // -------------------------------------------------------- Forge / NeoForge

    private static async Task<LoaderInstallResult> InstallWithInstallerAsync(
        string minecraftDirectory,
        string minecraftVersion,
        string loader,
        string loaderVersion,
        LoaderInstallOptions options,
        CancellationToken cancellationToken)
    {
        var client = options.Client ?? DownloadClient.Shared;
        var displayName = loader == "neoforge" ? "NeoForge" : "Forge";

        var installerUrls = BuildInstallerUrls(loader, minecraftVersion, loaderVersion);
        var installerName = $"{loader}-{minecraftVersion}-{loaderVersion}-installer.jar";
        var installerPath = Path.Combine(
            AppSettings.DataDirectory, "cache", "installers", installerName);

        options.Progress?.Report(new LoaderInstallProgress(
            $"正在下载 {displayName} 安装器…", 5, MinecraftDownloadStage.Preparing));

        var downloadUrls = installerUrls
            .SelectMany(url => DownloadUrlResolver.Build(url, options.Mirror))
            .ToList();

        await client.DownloadFileAsync(
            downloadUrls,
            installerPath,
            cancellationToken: cancellationToken);

        LauncherLog.Info($"{displayName} 安装器：{installerPath}，Java：{options.JavaPath}");

        if (string.IsNullOrWhiteSpace(options.JavaPath))
            throw new InvalidOperationException($"运行 {displayName} 安装器需要 Java，但没有找到可用的 Java。");

        var versionsRoot = Path.Combine(minecraftDirectory, "versions");
        var before = SnapshotVersions(versionsRoot);

        // Forge / NeoForge 安装器会检查 launcher_profiles.json，缺失时直接报错退出。
        EnsureLauncherProfiles(minecraftDirectory);

        options.Progress?.Report(new LoaderInstallProgress(
            $"正在运行 {displayName} 安装器…", null, MinecraftDownloadStage.Files));

        var (exitCode, output) = await RunInstallerAsync(
            options.JavaPath!, installerPath, minecraftDirectory, options.Log, cancellationToken);

        // 部分安装器在失败时仍然返回 0，需要同时检查输出。
        var failed = exitCode != 0 ||
                     output.Any(line => line.Contains(
                         "There was an error during installation",
                         StringComparison.OrdinalIgnoreCase));

        if (failed)
        {
            var detail = string.Join(Environment.NewLine, output.TakeLast(12));
            throw new InvalidOperationException(
                $"{displayName} 安装器未完成安装（退出码 {exitCode}）。{Environment.NewLine}{detail}");
        }

        var versionId = DetectInstalledVersion(
            versionsRoot, before, loader, loaderVersion);

        if (versionId is null)
        {
            var detail = string.Join(Environment.NewLine, output.TakeLast(12));
            throw new InvalidOperationException(
                $"{displayName} 安装器已结束，但没有找到新生成的版本目录。{Environment.NewLine}{detail}");
        }

        options.Progress?.Report(new LoaderInstallProgress(
            $"{displayName} 安装完成", 100, MinecraftDownloadStage.Finalizing));

        var finalId = VersionNaming.RenameInstalledVersion(
            minecraftDirectory, versionId, options.VersionName);

        return new LoaderInstallResult(
            finalId, Path.Combine(versionsRoot, finalId));
    }

    private static async Task<(int ExitCode, IReadOnlyList<string> Output)> RunInstallerAsync(
        string javaPath,
        string installerPath,
        string minecraftDirectory,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = javaPath,
            WorkingDirectory = minecraftDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("-jar");
        startInfo.ArgumentList.Add(installerPath);
        startInfo.ArgumentList.Add("--installClient");
        startInfo.ArgumentList.Add(minecraftDirectory);

        using var process = new Process { StartInfo = startInfo };
        var output = new ConcurrentQueue<string>();

        void Append(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            output.Enqueue(line);
            while (output.Count > 60) output.TryDequeue(out _);
            LauncherLog.Installer(line);
            log?.Report(line);
        }

        process.OutputDataReceived += (_, args) => Append(args.Data);
        process.ErrorDataReceived += (_, args) => Append(args.Data);

        if (!process.Start())
            throw new InvalidOperationException("无法启动加载器安装器。");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 进程可能已退出。
            }

            throw;
        }

        // 等待异步输出读完。
        process.WaitForExit();

        return (process.ExitCode, output.ToArray());
    }

    private static IReadOnlyList<string> BuildInstallerUrls(
        string loader,
        string minecraftVersion,
        string loaderVersion)
    {
        if (loader == "forge")
        {
            // 1.12.2 之前的 Forge 版本在 maven 中带 -<mc> 后缀，两种命名都尝试。
            var full = $"{minecraftVersion}-{loaderVersion}";
            return new[]
            {
                $"https://maven.minecraftforge.net/net/minecraftforge/forge/{full}/forge-{full}-installer.jar",
                $"https://maven.minecraftforge.net/net/minecraftforge/forge/{full}-{minecraftVersion}/" +
                $"forge-{full}-{minecraftVersion}-installer.jar",
            };
        }

        if (string.Equals(minecraftVersion, "1.20.1", StringComparison.OrdinalIgnoreCase))
        {
            var full = $"1.20.1-{loaderVersion}";
            return new[]
            {
                $"https://maven.neoforged.net/releases/net/neoforged/forge/{full}/forge-{full}-installer.jar",
            };
        }

        return new[]
        {
            $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{loaderVersion}/" +
            $"neoforge-{loaderVersion}-installer.jar",
        };
    }

    private static void EnsureLauncherProfiles(string minecraftDirectory)
    {
        try
        {
            var path = Path.Combine(minecraftDirectory, "launcher_profiles.json");
            if (File.Exists(path)) return;

            Directory.CreateDirectory(minecraftDirectory);
            File.WriteAllText(path,
                "{\n  \"profiles\": {},\n  \"settings\": {},\n  \"version\": 3\n}\n");
        }
        catch (Exception)
        {
            // 写入失败时继续，安装器会给出更明确的错误。
        }
    }

    private static HashSet<string> SnapshotVersions(string versionsRoot)
    {
        try
        {
            return Directory.Exists(versionsRoot)
                ? Directory.GetDirectories(versionsRoot)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrEmpty(name))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)!
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string? DetectInstalledVersion(
        string versionsRoot,
        HashSet<string> before,
        string loader,
        string loaderVersion)
    {
        try
        {
            if (!Directory.Exists(versionsRoot)) return null;

            var names = Directory.GetDirectories(versionsRoot)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Cast<string>()
                .ToList();

            var candidates = names
                .Where(name => !before.Contains(name) || name.Contains(loader, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(name => name.Contains(loaderVersion, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(name => Directory.GetLastWriteTimeUtc(Path.Combine(versionsRoot, name)))
                .ToList();

            foreach (var name in candidates)
            {
                var json = Path.Combine(versionsRoot, name, name + ".json");
                if (File.Exists(json)) return name;
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- 工具

    private static string? DeriveMavenPath(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var parts = name.Split(':');
        if (parts.Length < 3) return null;

        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 && parts[3].Length > 0 ? "-" + parts[3] : string.Empty;

        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
    }

    private sealed class FabricProfile
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("libraries")] public List<FabricLibrary>? Libraries { get; set; }
    }

    private sealed class FabricLibrary
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
    }
}
