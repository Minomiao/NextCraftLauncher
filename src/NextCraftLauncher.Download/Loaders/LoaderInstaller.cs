using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>
    /// 安装后的版本目录名称。加载器安装必须提供：调用方需要先把原版文件下载到
    /// <c>versions/{VersionName}</c>，安装器只把加载器配置合并进这一份版本 JSON。
    /// </summary>
    public string? VersionName { get; init; }

    /// <summary>可选的 Fabric API 版本；安装完成后会下载到 mods 目录。</summary>
    public string? FabricApiVersion { get; init; }

    /// <summary>版本隔离：为 true 时 mods 放在版本目录下，否则放在游戏根目录。</summary>
    public bool IsolateVersion { get; init; }

    public IProgress<LoaderInstallProgress>? Progress { get; init; }

    /// <summary>安装器实时输出的日志行（仅 Forge / NeoForge）。</summary>
    public IProgress<string>? Log { get; init; }

    public DownloadClient? Client { get; init; }
}

public sealed record LoaderInstallProgress(
    string Text,
    double? Percent,
    MinecraftDownloadStage Stage)
{
    /// <summary>已下载字节数（累计）；非下载阶段为 null，界面据此计算下载速度。</summary>
    public long? BytesDownloaded { get; init; }
}

public sealed record LoaderInstallResult(string VersionId, string VersionDirectory);

/// <summary>
/// 安装模组加载器：
/// <list type="bullet">
/// <item>Fabric：下载官方 profile JSON，再按其中的 maven 坐标补齐库文件；</item>
/// <item>Forge / NeoForge：下载官方 installer，在临时游戏目录里运行 <c>--installClient</c>。</item>
/// </list>
/// 与原版一样只有一个版本目录：调用方先把原版文件下载到 <c>versions/{VersionName}</c>，
/// 这里再把加载器的 JSON 合并进同一份文件（去掉 inheritsFrom），
/// 所以真实的 <c>versions</c> 下不会多出一个只有原版内容的版本。
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

        if (string.IsNullOrWhiteSpace(options.VersionName))
        {
            throw new ArgumentException(
                "安装加载器时必须指定版本名称（LoaderInstallOptions.VersionName）。",
                nameof(options));
        }

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

        var profileId = string.IsNullOrWhiteSpace(profile.Id)
            ? $"fabric-loader-{loaderVersion}-{minecraftVersion}"
            : profile.Id!;

        var versionId = ResolveTargetName(options, profileId);
        var versionDirectory = Path.Combine(minecraftDirectory, "versions", versionId);
        var versionJsonPath = Path.Combine(versionDirectory, versionId + ".json");

        // 原版 JSON 由调用方先下载到同一目录；Fabric profile 只有 inheritsFrom 以外的部分，
        // 合并成一份自包含的版本文件后才能删掉对原版目录的依赖。
        var vanillaBytes = await ReadVanillaJsonAsync(versionJsonPath, cancellationToken);

        Directory.CreateDirectory(versionDirectory);

        var libraries = (profile.Libraries ?? new List<FabricLibrary>())
            .Where(library => !string.IsNullOrWhiteSpace(library.Name))
            .ToList();

        LauncherLog.Info($"Fabric profile {profileId}，需要 {libraries.Count} 个库文件");

        var completed = 0;
        var total = Math.Max(libraries.Count, 1);
        var knownBytes = libraries.Sum(library => Math.Max(0, library.Size));
        var transferredBytes = 0L;
        var gate = new SemaphoreSlim(16);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // 有大小信息时按字节推进（可看出速度和进度），否则退回按文件数推进。
        void ReportFabricProgress()
        {
            var done = Volatile.Read(ref completed);
            var bytes = Interlocked.Read(ref transferredBytes);

            // Fabric 的 profile 里并非每个库都带大小，按字节算的百分比会提前打满，
            // 因此百分比按完成文件数走，字节数只用于显示速度与已下量。
            var percent = Math.Min(99, done / (double)total * 100);
            var amount = bytes > 0 ? $"（已下 {bytes / 1048576.0:0.0} MB）" : string.Empty;

            options.Progress?.Report(new LoaderInstallProgress(
                $"正在下载 Fabric 库文件 {Math.Min(done + 1, total)}/{libraries.Count}{amount}…",
                percent,
                MinecraftDownloadStage.Files)
            {
                BytesDownloaded = bytes,
            });
        }

        var tasks = libraries.Select(async library =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var lastBytes = 0L;
                var lastReportTicks = 0L;
                var fileProgress = new ForwardProgress<DownloadProgress>(report =>
                {
                    var delta = report.BytesReceived - lastBytes;
                    if (delta <= 0) return;
                    lastBytes = report.BytesReceived;
                    Interlocked.Add(ref transferredBytes, delta);

                    // 并发后回调很密集，限制刷新频率。
                    var now = Environment.TickCount64;
                    if (now - lastReportTicks < 100) return;
                    lastReportTicks = now;
                    ReportFabricProgress();
                });

                await DownloadLibraryAsync(
                    client, minecraftDirectory, library, options.Mirror, fileProgress, cancellationToken);

                // 文件已存在而跳过、或尾部回调被节流时，补齐剩余字节，保证进度不会倒退或卡住。
                var remaining = Math.Max(0, library.Size - lastBytes);
                if (remaining > 0) Interlocked.Add(ref transferredBytes, remaining);

                Interlocked.Increment(ref completed);
                ReportFabricProgress();
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);

        LauncherLog.Info(
            $"Fabric 库文件 {libraries.Count} 个（{knownBytes / 1048576.0:0.0} MB）已处理，用时 {clock.ElapsedMilliseconds} ms");

        options.Progress?.Report(new LoaderInstallProgress(
            "正在写入 Fabric 版本配置…", 99, MinecraftDownloadStage.Finalizing));

        var mergedBytes = BuildMergedVersionJson(vanillaBytes, profileBytes, versionId, minecraftVersion);
        await File.WriteAllBytesAsync(versionJsonPath, mergedBytes, cancellationToken);

        if (!string.IsNullOrWhiteSpace(options.FabricApiVersion))
            await InstallFabricApiAsync(client, minecraftDirectory, versionDirectory, options, cancellationToken);

        options.Progress?.Report(new LoaderInstallProgress(
            "Fabric 安装完成", 100, MinecraftDownloadStage.Finalizing));

        return new LoaderInstallResult(versionId, versionDirectory);
    }

    /// <summary>把选中的 Fabric API 下载到该版本对应的 mods 目录。</summary>
    private static async Task InstallFabricApiAsync(
        DownloadClient client,
        string minecraftDirectory,
        string versionDirectory,
        LoaderInstallOptions options,
        CancellationToken cancellationToken)
    {
        var apiVersion = options.FabricApiVersion!;
        var encoded = Uri.EscapeDataString(apiVersion);
        var url = "https://maven.fabricmc.net/net/fabricmc/fabric-api/fabric-api/" +
                  $"{encoded}/fabric-api-{encoded}.jar";

        var modsDirectory = Path.Combine(
            options.IsolateVersion ? versionDirectory : minecraftDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);

        var destination = Path.Combine(modsDirectory, $"fabric-api-{apiVersion}.jar");

        options.Progress?.Report(new LoaderInstallProgress(
            $"正在下载 Fabric API {apiVersion}…", 99, MinecraftDownloadStage.Finalizing));

        var lastReportTicks = 0L;
        var apiProgress = new ForwardProgress<DownloadProgress>(report =>
        {
            var now = Environment.TickCount64;
            if (now - lastReportTicks < 100) return;
            lastReportTicks = now;

            options.Progress?.Report(new LoaderInstallProgress(
                $"正在下载 Fabric API {apiVersion}…", 99, MinecraftDownloadStage.Finalizing)
            {
                BytesDownloaded = report.BytesReceived,
            });
        });

        await client.DownloadFileAsync(
            DownloadUrlResolver.Build(url, options.Mirror),
            destination,
            progress: apiProgress,
            cancellationToken: cancellationToken);

        LauncherLog.Info($"Fabric API {apiVersion} → {destination}");
    }

    private static async Task DownloadLibraryAsync(
        DownloadClient client,
        string minecraftDirectory,
        FabricLibrary library,
        DownloadMirror mirror,
        IProgress<DownloadProgress>? progress,
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
            progress,
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

        var versionId = VersionNaming.Normalize(options.VersionName);
        var versionDirectory = Path.Combine(minecraftDirectory, "versions", versionId);
        var versionJsonPath = Path.Combine(versionDirectory, versionId + ".json");
        var versionJarPath = Path.Combine(versionDirectory, versionId + ".jar");

        // 调用方已经把原版文件下载到目标目录。安装器需要的父版本 json / jar 只放进临时目录，
        // 安装完成后合并进目标版本的 JSON，真实的 versions 下不会多出一个普通版本。
        var vanillaBytes = await ReadVanillaJsonAsync(versionJsonPath, cancellationToken);

        var installerUrls = BuildInstallerUrls(loader, minecraftVersion, loaderVersion);
        var installerName = $"{loader}-{minecraftVersion}-{loaderVersion}-installer.jar";
        var installerPath = Path.Combine(
            AppSettings.DataDirectory, "cache", "installers", installerName);

        options.Progress?.Report(new LoaderInstallProgress(
            $"正在下载 {displayName} 安装器…", 0, MinecraftDownloadStage.Preparing));

        var downloadUrls = installerUrls
            .SelectMany(url => DownloadUrlResolver.Build(url, options.Mirror))
            .ToList();

        // 安装器 jar 占进度的 0-40%，按字节上报，避免大文件下载期间进度条和速度一直倒退。
        var jarProgress = new ForwardProgress<DownloadProgress>(report =>
        {
            var fraction = report.TotalBytes is > 0
                ? Math.Min(1, report.BytesReceived / (double)report.TotalBytes.Value)
                : 0;

            options.Progress?.Report(new LoaderInstallProgress(
                $"正在下载 {displayName} 安装器…", fraction * 30, MinecraftDownloadStage.Preparing)
            {
                BytesDownloaded = report.BytesReceived,
            });
        });

        await client.DownloadFileAsync(
            downloadUrls,
            installerPath,
            progress: jarProgress,
            cancellationToken: cancellationToken);

        LauncherLog.Info($"{displayName} 安装器：{installerPath}，Java：{options.JavaPath}");

        if (string.IsNullOrWhiteSpace(options.JavaPath))
            throw new InvalidOperationException($"运行 {displayName} 安装器需要 Java，但没有找到可用的 Java。");

        // 安装器（Java 进程）会自己去官方 maven 下载它需要的库，在 DNS 被污染的网络下会卡住甚至失败。
        // 先用启动器自己的下载核心把这些库补齐，安装器校验通过后就不再访问网络。
        options.Progress?.Report(new LoaderInstallProgress(
            $"正在补齐 {displayName} 安装器依赖…", 30, MinecraftDownloadStage.Preparing));

        // 临时游戏目录：安装器的产物先生在这里，最后只把合并后的版本配置写回真实目录。
        var tempRoot = Path.Combine(
            Path.GetTempPath(), "ncl-loader-" + Guid.NewGuid().ToString("N"));

        try
        {
            // 安装器会校验父版本：把原版的 json / jar 放到临时目录的同名位置。
            var parentVersionDirectory = Path.Combine(tempRoot, "versions", minecraftVersion);
            Directory.CreateDirectory(parentVersionDirectory);
            await File.WriteAllBytesAsync(
                Path.Combine(parentVersionDirectory, minecraftVersion + ".json"),
                vanillaBytes,
                cancellationToken);

            if (File.Exists(versionJarPath))
            {
                File.Copy(
                    versionJarPath,
                    Path.Combine(parentVersionDirectory, minecraftVersion + ".jar"),
                    overwrite: true);
            }

            await PrepareInstallerLibrariesAsync(
                client, tempRoot, installerPath, options.Mirror, displayName,
                DefaultMavenBase(loader), options.Progress, cancellationToken);

            // Forge / NeoForge 安装器会检查 launcher_profiles.json，缺失时直接报错退出。
            EnsureLauncherProfiles(tempRoot);

            var versionsRoot = Path.Combine(tempRoot, "versions");
            var before = SnapshotVersions(versionsRoot);

            options.Progress?.Report(new LoaderInstallProgress(
                $"正在运行 {displayName} 安装器…", null, MinecraftDownloadStage.Files));

            var (exitCode, output) = await RunInstallerAsync(
                options.JavaPath!, installerPath, tempRoot, options.Log, cancellationToken);

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

            var installedId = DetectInstalledVersion(
                versionsRoot, before, loader, loaderVersion);

            if (installedId is null)
            {
                var detail = string.Join(Environment.NewLine, output.TakeLast(12));
                throw new InvalidOperationException(
                    $"{displayName} 安装器已结束，但没有找到新生成的版本目录。{Environment.NewLine}{detail}");
            }

            var installedDirectory = Path.Combine(versionsRoot, installedId);
            var loaderBytes = await File.ReadAllBytesAsync(
                Path.Combine(installedDirectory, installedId + ".json"), cancellationToken);

            options.Progress?.Report(new LoaderInstallProgress(
                "正在写入版本配置…", 99, MinecraftDownloadStage.Finalizing));

            // 合并成一份自包含的版本文件，去掉 inheritsFrom，这样不再依赖一个单独的原版版本。
            var mergedBytes = BuildMergedVersionJson(vanillaBytes, loaderBytes, versionId, minecraftVersion);
            Directory.CreateDirectory(versionDirectory);
            await File.WriteAllBytesAsync(versionJsonPath, mergedBytes, cancellationToken);

            // 旧版 Forge 安装器会生成打过补丁的客户端 jar；有就用它替换原来的原版 jar。
            var installedJarPath = Path.Combine(installedDirectory, installedId + ".jar");
            if (File.Exists(installedJarPath))
                File.Copy(installedJarPath, versionJarPath, overwrite: true);

            // 安装器补齐的库最终仍要放进真实的 libraries；文件可能有几十 MB，
            // 不在界面线程上拷贝，避免收尾时又卡住一下。
            await Task.Run(
                () => CopyDirectory(
                    Path.Combine(tempRoot, "libraries"),
                    Path.Combine(minecraftDirectory, "libraries")),
                cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }

        options.Progress?.Report(new LoaderInstallProgress(
            $"{displayName} 安装完成", 100, MinecraftDownloadStage.Finalizing));

        return new LoaderInstallResult(versionId, versionDirectory);
    }

    /// <summary>
    /// 按安装器内置的 <c>install_profile.json</c> 预先下载它需要的库。
    /// 安装器发现文件已存在且校验通过时就不会再联网，因此能绕开它自己请求官方 maven 的问题。
    /// </summary>
    private static async Task PrepareInstallerLibrariesAsync(
        DownloadClient client,
        string minecraftDirectory,
        string installerPath,
        DownloadMirror mirror,
        string displayName,
        string defaultMavenBase,
        IProgress<LoaderInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var libraries = ReadInstallerLibraries(installerPath);
        if (libraries.Count == 0) return;

        // 安装器依赖只有几 MB 但文件数不少，串行下载时每个文件都要等一次完整往返。
        // 用有限并发并行拉，与版本文件下载共用同一套下载核心。
        var total = libraries.Count;
        var downloaded = 0;
        var transferredBytes = 0L;
        var completed = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var gate = new SemaphoreSlim(Math.Min(16, total));

        var tasks = libraries.Select(async (library, index) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var artifact = library.Downloads?.Artifact;
                var path = artifact?.Path;
                if (string.IsNullOrWhiteSpace(path))
                    path = DeriveMavenPath(library.Name);
                if (string.IsNullOrWhiteSpace(path)) return;

                var url = artifact?.Url;
                if (string.IsNullOrWhiteSpace(url))
                {
                    var baseUrl = string.IsNullOrWhiteSpace(library.Url)
                        ? defaultMavenBase
                        : library.Url!;
                    url = baseUrl.TrimEnd('/') + "/" + path;
                }

                var destination = Path.Combine(
                    minecraftDirectory, "libraries", path.Replace('/', Path.DirectorySeparatorChar));

                var lastBytes = 0L;
                var lastReportTicks = 0L;
                var fileProgress = new ForwardProgress<DownloadProgress>(report =>
                {
                    // 并发后回调密度是原来的十几倍，限制一下刷新频率。
                    var now = Environment.TickCount64;
                    if (now - lastReportTicks < 100) return;
                    lastReportTicks = now;

                    var delta = report.BytesReceived - lastBytes;
                    if (delta != 0)
                    {
                        lastBytes = report.BytesReceived;
                        Interlocked.Add(ref transferredBytes, delta);
                    }

                    var done = Volatile.Read(ref completed);
                    progress?.Report(new LoaderInstallProgress(
                        $"正在准备 {displayName} 安装器依赖 {Math.Min(done + 1, total)}/{total}…",
                        30 + done / (double)total * 25,
                        MinecraftDownloadStage.Preparing)
                    {
                        BytesDownloaded = Interlocked.Read(ref transferredBytes),
                    });
                });

                try
                {
                    var sha1 = string.IsNullOrWhiteSpace(artifact?.Sha1) ? null : artifact!.Sha1;

                    // 已存在且校验通过时 DownloadFileAsync 会直接返回，不产生网络请求。
                    await client.DownloadFileAsync(
                        DownloadUrlResolver.Build(url!, mirror),
                        destination,
                        sha1,
                        artifact is { Size: > 0 } ? artifact.Size : null,
                        fileProgress,
                        cancellationToken);

                    Interlocked.Increment(ref downloaded);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // 单个库失败不阻断安装，安装器自己还会再试一次。
                    LauncherLog.Warn($"安装器依赖 {path} 下载失败：{exception.Message}");
                }

                var finished = Interlocked.Increment(ref completed);
                progress?.Report(new LoaderInstallProgress(
                    $"正在准备 {displayName} 安装器依赖 {finished}/{total}…",
                    30 + finished / (double)total * 25,
                    MinecraftDownloadStage.Preparing)
                {
                    BytesDownloaded = Interlocked.Read(ref transferredBytes),
                });
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);

        if (downloaded > 0)
            LauncherLog.Info($"安装器依赖：已准备 {downloaded} 个文件，用时 {clock.ElapsedMilliseconds} ms");
    }

    /// <summary>同步转发进度的包装类，真正的线程切换由调用方的 Progress&lt;T&gt; 负责。</summary>
    private sealed class ForwardProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private static IReadOnlyList<InstallerLibrary> ReadInstallerLibraries(string installerPath)
    {
        var libraries = new List<InstallerLibrary>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var archive = ZipFile.OpenRead(installerPath);

            // install_profile.json 是安装器自己需要的工具库；version.json 是装完后要运行的库。
            // 两者都要提前拉：安装器会自己下载 version.json 里的库，而它的下载器在这类网络下很慢。
            foreach (var entryName in new[] { "install_profile.json", "version.json" })
            {
                var entry = archive.GetEntry(entryName);
                if (entry is null) continue;

                InstallerProfile? profile;
                using (var stream = entry.Open())
                {
                    profile = JsonSerializer.Deserialize<InstallerProfile>(stream, JsonOptions);
                }

                foreach (var library in profile?.Libraries ?? new List<InstallerLibrary>())
                {
                    if (library.Clientreq == false) continue;
                    if (!seen.Add(library.Name ?? string.Empty)) continue;
                    libraries.Add(library);
                }
            }

            return libraries;
        }
        catch (Exception exception)
        {
            LauncherLog.Warn($"读取安装器配置失败：{exception.Message}");
            return Array.Empty<InstallerLibrary>();
        }
    }

    private sealed class InstallerProfile
    {
        public List<InstallerLibrary>? Libraries { get; set; }
    }

    private sealed class InstallerLibrary
    {
        public string? Name { get; set; }

        /// <summary>旧版安装配置的字段，false 表示只在服务端安装时使用。</summary>
        public bool? Clientreq { get; set; }

        public string? Url { get; set; }

        public InstallerLibraryDownloads? Downloads { get; set; }
    }

    private sealed class InstallerLibraryDownloads
    {
        public InstallerLibraryArtifact? Artifact { get; set; }
    }

    private sealed class InstallerLibraryArtifact
    {
        public string? Path { get; set; }
        public string? Url { get; set; }
        public string? Sha1 { get; set; }
        public long Size { get; set; }
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

    private static string DefaultMavenBase(string loader)
        => loader == "forge"
            ? "https://maven.minecraftforge.net/"
            : "https://maven.neoforged.net/releases/";

    /// <summary>目标版本名：优先用调用方指定的名称，否则退回加载器默认 id。</summary>
    private static string ResolveTargetName(LoaderInstallOptions options, string fallback)
    {
        var name = VersionNaming.Normalize(options.VersionName);
        return name.Length > 0 ? name : VersionNaming.Normalize(fallback);
    }

    private static async Task<byte[]> ReadVanillaJsonAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"找不到原版版本配置：{path}。安装加载器前需要先把原版文件下载到该目录。", path);
        }

        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    /// <summary>
    /// 把加载器 JSON 合并进原版 JSON，生成一份自包含的版本文件：
    /// 库与参数按继承链会得到的顺序拼接，最后去掉 <c>inheritsFrom</c>。
    /// 这样 <c>versions</c> 下只需要一个目录，不会因为父版本而多出一个普通版本。
    /// </summary>
    private static byte[] BuildMergedVersionJson(
        byte[] vanillaBytes,
        byte[] loaderBytes,
        string id,
        string clientVersion)
    {
        var vanilla = JsonNode.Parse(vanillaBytes) as JsonObject
                      ?? throw new InvalidDataException("原版版本 JSON 格式不正确。");
        var loader = JsonNode.Parse(loaderBytes) as JsonObject
                     ?? throw new InvalidDataException("加载器版本 JSON 格式不正确。");

        // 库：加载器优先，并按 group:artifact:classifier 去重，与原版继承链的解析顺序一致。
        var libraries = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (loader["libraries"] is JsonArray loaderLibraries)
        {
            foreach (var library in loaderLibraries)
            {
                if (library is not JsonObject item) continue;
                libraries.Add(item.DeepClone());
                if (LibraryKey(item["name"]?.GetValue<string>()) is { } key) seen.Add(key);
            }
        }

        if (vanilla["libraries"] is JsonArray vanillaLibraries)
        {
            foreach (var library in vanillaLibraries)
            {
                if (library is not JsonObject item) continue;
                var key = LibraryKey(item["name"]?.GetValue<string>());
                if (key is not null && !seen.Add(key)) continue;
                libraries.Add(item.DeepClone());
            }
        }

        vanilla["libraries"] = libraries;

        // 参数：原版在前、加载器在后，与原版继承链的拼接顺序一致。
        if (loader["arguments"] is JsonObject loaderArguments)
        {
            if (vanilla["arguments"] is not JsonObject vanillaArguments)
            {
                vanillaArguments = new JsonObject();
                vanilla["arguments"] = vanillaArguments;
            }

            MergeArgumentArray(vanillaArguments, loaderArguments, "game");
            MergeArgumentArray(vanillaArguments, loaderArguments, "jvm");
            MergeArgumentArray(vanillaArguments, loaderArguments, "default-user-jvm");
        }

        // 其余标量字段（mainClass、type、releaseTime 等）以加载器为准。
        foreach (var property in loader)
        {
            switch (property.Key)
            {
                case "id":
                case "inheritsFrom":
                case "jar":
                case "type":
                case "libraries":
                case "arguments":
                case "minecraftArguments":
                case "downloads":
                    continue;
            }

            vanilla[property.Key] = property.Value?.DeepClone();
        }

        // 旧版 Forge / Fabric 用 minecraftArguments（子版本优先）。
        if (loader["minecraftArguments"] is JsonNode legacyArguments)
            vanilla["minecraftArguments"] = legacyArguments.DeepClone();

        vanilla.Remove("inheritsFrom");
        vanilla.Remove("jar");
        vanilla["id"] = id;
        // 与 PCL2 一致：记下对应的原版版本，之后判断 Mod 兼容性时用。
        vanilla["clientVersion"] = clientVersion;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            vanilla.WriteTo(writer);

        LauncherLog.Info($"版本配置合并完成：{id}，共 {libraries.Count} 个库");
        return stream.ToArray();
    }

    private static void MergeArgumentArray(JsonObject target, JsonObject source, string key)
    {
        if (source[key] is not JsonArray sourceArray) return;

        if (target[key] is not JsonArray targetArray)
        {
            targetArray = new JsonArray();
            target[key] = targetArray;
        }

        foreach (var entry in sourceArray)
            targetArray.Add(entry?.DeepClone());
    }

    private static string? LibraryKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var parts = name.Split(':');
        if (parts.Length < 3) return name;

        var classifier = parts.Length > 3 ? parts[3] : string.Empty;
        return $"{parts[0]}:{parts[1]}:{classifier}";
    }

    /// <summary>安装器在临时目录里补齐的库并回真实 libraries；已存在且大小相同的文件直接跳过。</summary>
    private static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source)) return;

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(file).Length)
                continue;

            File.Copy(file, target, overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception)
            {
                if (attempt == 2)
                    LauncherLog.Warn($"清理临时目录失败：{path}，{exception.Message}");

                Thread.Sleep(200);
            }
        }
    }

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
