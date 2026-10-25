using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using NextCraftLauncher.Core.Minecraft;
using NextCraftLauncher.Core.Settings;
using NextCraftLauncher.Core.Util;

namespace NextCraftLauncher.Download.Minecraft;

public sealed class MinecraftVersionDownloadOptions
{
    public DownloadMirror Mirror { get; init; } = DownloadMirror.Automatic;

    public int MaxDegreeOfParallelism { get; init; } = 16;

    /// <summary>小文件（主要是 assets objects）的并发数；尾段大量小文件靠并发掩盖请求延迟。</summary>
    public int SmallFileMaxDegreeOfParallelism { get; init; } = 48;

    /// <summary>仅用于测试或“只补客户端/库”的场景。</summary>
    public bool DownloadAssets { get; init; } = true;

    /// <summary>assets objects 的基础地址；默认使用 Mojang 官方资源服务器。</summary>
    public string AssetObjectBaseUrl { get; init; } = "https://resources.download.minecraft.net/";

    public DownloadClient? Client { get; init; }
}

public sealed record MinecraftVersionDownloadProgress(
    int CompletedFiles,
    int TotalFiles,
    string? CurrentFile,
    long BytesDownloaded,
    long? TotalBytes)
{
    public MinecraftDownloadStage Stage { get; init; } = MinecraftDownloadStage.Files;

    /// <summary>按“下载成本”统计的已完成量，见 <see cref="TotalCost"/>。</summary>
    public long CompletedCost { get; init; }

    /// <summary>
    /// 进度成本总和：每个文件除字节数外，额外计入一次请求的固定开销。
    /// assets 里有三千多个十几 KB 的小文件，纯按字节算会让进度条最后 7% 停留很久。
    /// </summary>
    public long? TotalCost { get; init; }

    public double? Percent => TotalCost is > 0
        ? Math.Min(100, (double)CompletedCost / TotalCost.Value * 100)
        : TotalBytes is > 0
            ? Math.Min(100, (double)BytesDownloaded / TotalBytes.Value * 100)
            : TotalFiles > 0
                ? (double)CompletedFiles / TotalFiles * 100
                : null;
}

public enum MinecraftDownloadStage
{
    /// <summary>读取版本 JSON、资源索引等元数据。</summary>
    Preparing,

    /// <summary>下载客户端 jar、依赖库、natives 与 assets。</summary>
    Files,

    /// <summary>写入版本 JSON 与隔离配置。</summary>
    Finalizing,
}

public sealed record MinecraftVersionDownloadResult(
    string VersionId,
    string VersionDirectory,
    int TotalFiles,
    long TotalBytes,
    bool Isolated);

/// <summary>
/// 下载一个完整的 Mojang 游戏版本：版本 JSON、客户端 jar、依赖库、natives、
/// 资源索引与 assets objects，并写入版本隔离设置。
/// </summary>
public static class MinecraftVersionDownloader
{
    /// <summary>
    /// 每个文件除字节数外固定计入的进度成本，近似“一次请求的延迟 × 带宽”。
    /// 没有它的话，最后 3000 个几 KB 的 assets 只占进度条的 7%，会长时间卡在 93%。
    /// </summary>
    private const long MinimumFileCost = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static async Task<MinecraftVersionDownloadResult> DownloadAsync(
        string minecraftDirectory,
        MinecraftVersionEntry entry,
        bool isolate,
        IProgress<MinecraftVersionDownloadProgress>? progress = null,
        MinecraftVersionDownloadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entry.Id))
            throw new ArgumentException("版本 ID 不能为空。", nameof(entry));
        if (string.IsNullOrWhiteSpace(entry.Url))
            throw new ArgumentException($"版本 {entry.Id} 缺少 JSON 地址。", nameof(entry));

        options ??= new MinecraftVersionDownloadOptions();
        var client = options.Client ?? DownloadClient.Shared;

        var versionId = entry.Id;
        var versionDirectory = Path.Combine(minecraftDirectory, "versions", versionId);
        Directory.CreateDirectory(versionDirectory);

        // 1. 版本 JSON：本地已存在且校验通过时直接复用，补文件时省掉一次往返。
        var versionJsonPath = Path.Combine(versionDirectory, versionId + ".json");
        var versionJsonBytes = TryReadVerified(versionJsonPath, entry.Sha1, expectedSize: null)
                               ?? await client.DownloadBytesAutoAsync(
                                   DownloadUrlResolver.Build(entry.Url, options.Mirror),
                                   options.Mirror,
                                   string.IsNullOrWhiteSpace(entry.Sha1) ? null : entry.Sha1,
                                   expectedSize: null,
                                   cancellationToken: cancellationToken);

        var versionInfo = JsonSerializer.Deserialize<VersionInfo>(versionJsonBytes, JsonOptions)
                          ?? throw new InvalidDataException("无法解析版本 JSON。");

        // 2. 收集文件
        var work = new List<DownloadWork>();
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var environment = LaunchEnvironment.Current;

        void Add(string displayName, string destination, string? url, string? sha1, long size)
        {
            if (string.IsNullOrWhiteSpace(url)) return;

            var fullPath = Path.GetFullPath(destination);
            if (!destinations.Add(fullPath)) return;

            work.Add(new DownloadWork(
                displayName,
                fullPath,
                DownloadUrlResolver.Build(url, options.Mirror),
                string.IsNullOrWhiteSpace(sha1) ? null : sha1,
                Math.Max(0, size)));
        }

        var clientArtifact = versionInfo.Downloads?.Client;
        if (clientArtifact is not null)
        {
            Add(
                "客户端 jar",
                Path.Combine(versionDirectory, versionId + ".jar"),
                clientArtifact.Url,
                clientArtifact.Sha1,
                clientArtifact.Size);
        }

        foreach (var library in versionInfo.Libraries ?? new List<LibraryInfo>())
        {
            if (!RuleEvaluator.IsAllowed(library.Rules, environment)) continue;

            var artifact = library.Downloads?.Artifact;
            if (artifact?.Path is { Length: > 0 } artifactPath &&
                artifact.Url is { Length: > 0 } artifactUrl)
            {
                Add(
                    library.Name ?? artifactPath,
                    Path.Combine(minecraftDirectory, "libraries", Normalize(artifactPath)),
                    artifactUrl,
                    artifact.Sha1,
                    artifact.Size);
            }
            else if (library.Downloads?.Classifiers is null)
            {
                // Fabric / Forge 的 maven 条目没有 downloads，按坐标推导。
                var mavenPath = DeriveMavenPath(library.Name);
                if (mavenPath is not null)
                {
                    var baseUrl = string.IsNullOrWhiteSpace(library.Url)
                        ? "https://libraries.minecraft.net/"
                        : EnsureTrailingSlash(library.Url);

                    Add(
                        library.Name ?? mavenPath,
                        Path.Combine(minecraftDirectory, "libraries", Normalize(mavenPath)),
                        baseUrl + mavenPath,
                        null,
                        0);
                }
            }

            if (library.Natives is { Count: > 0 } natives &&
                library.Downloads?.Classifiers is { Count: > 0 } classifiers)
            {
                var native = SelectNativeArtifact(natives, classifiers, environment);
                if (native?.Path is { Length: > 0 } nativePath &&
                    native.Url is { Length: > 0 } nativeUrl)
                {
                    Add(
                        (library.Name ?? "natives") + " natives",
                        Path.Combine(minecraftDirectory, "libraries", Normalize(nativePath)),
                        nativeUrl,
                        native.Sha1,
                        native.Size);
                }
            }
        }

        // 3. 资源索引（需要先拿到内容才能展开 assets objects）
        byte[]? assetIndexBytes = null;
        var assetIndex = versionInfo.AssetIndex;
        if (assetIndex is not null &&
            !string.IsNullOrWhiteSpace(assetIndex.Url) &&
            !string.IsNullOrWhiteSpace(assetIndex.Id))
        {
            var assetIndexPath = Path.Combine(minecraftDirectory, "assets", "indexes", assetIndex.Id + ".json");
            assetIndexBytes = TryReadVerified(
                assetIndexPath,
                assetIndex.Sha1,
                assetIndex.Size > 0 ? assetIndex.Size : null);

            if (assetIndexBytes is null)
            {
                assetIndexBytes = await client.DownloadBytesAutoAsync(
                    DownloadUrlResolver.Build(assetIndex.Url, options.Mirror),
                    options.Mirror,
                    string.IsNullOrWhiteSpace(assetIndex.Sha1) ? null : assetIndex.Sha1,
                    assetIndex.Size > 0 ? assetIndex.Size : null,
                    cancellationToken);

                WriteFile(assetIndexPath, assetIndexBytes);
            }

            if (options.DownloadAssets)
            {
                var index = JsonSerializer.Deserialize<AssetIndexManifest>(assetIndexBytes, JsonOptions);
                if (index?.Objects is { Count: > 0 } objects)
                {
                    foreach (var (name, asset) in objects)
                    {
                        if (asset.Hash is not { Length: >= 2 } hash) continue;

                        var prefix = hash[..2];
                        var assetBase = options.AssetObjectBaseUrl.TrimEnd('/');
                        Add(
                            name,
                            Path.Combine(minecraftDirectory, "assets", "objects", prefix, hash),
                            $"{assetBase}/{prefix}/{hash}",
                            hash,
                            asset.Size);
                    }
                }
            }
        }

        // 4. 进度与并发下载
        var completedFiles = assetIndexBytes is null ? 0 : 1;
        var completedBytes = assetIndexBytes?.LongLength ?? 0;
        var completedCost = assetIndexBytes?.LongLength ?? 0;
        var totalFiles = work.Count + completedFiles + 1; // + 版本 JSON
        var totalBytes = work.Sum(item => item.Size) + completedBytes + versionJsonBytes.LongLength;
        var totalCost = work.Sum(item => item.Cost)
                        + (assetIndexBytes?.LongLength ?? 0)
                        + Math.Max(versionJsonBytes.LongLength, MinimumFileCost);

        var inFlightBytes = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        var inFlightCost = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        var lastReportTicks = 0L;
        var reportGate = new object();
        var progressGate = new object();
        var stage = MinecraftDownloadStage.Preparing;

        void ReportProgress(string? currentFile, bool force)
        {
            var now = Environment.TickCount64;
            lock (reportGate)
            {
                if (!force && now - lastReportTicks < 200) return;
                lastReportTicks = now;
            }

            // 在同一把锁里取快照，避免“文件完成”与“在途字节”两个更新之间出现短暂的回退。
            int files;
            long bytes;
            long cost;
            lock (progressGate)
            {
                files = completedFiles;
                bytes = completedBytes;
                cost = completedCost;
                foreach (var value in inFlightBytes.Values) bytes += value;
                foreach (var value in inFlightCost.Values) cost += value;
            }

            progress?.Report(new MinecraftVersionDownloadProgress(
                files,
                totalFiles,
                currentFile,
                bytes,
                totalBytes > 0 ? totalBytes : null)
            {
                Stage = stage,
                CompletedCost = cost,
                TotalCost = totalCost,
            });
        }

        if (assetIndexBytes is not null)
            ReportProgress("资源索引", force: true);

        stage = MinecraftDownloadStage.Files;

        async ValueTask DownloadItemAsync(DownloadWork item, CancellationToken token)
        {
            // 没有哈希/大小的 maven 文件无法校验，已存在则直接跳过。
            if (item.Sha1 is null && item.Size <= 0 && File.Exists(item.Destination))
            {
                lock (progressGate)
                {
                    completedFiles++;
                    completedCost += item.Cost;
                }

                ReportProgress(item.DisplayName, force: false);
                return;
            }

            var fileProgress = new InlineProgress<DownloadProgress>(report =>
            {
                inFlightBytes[item.Destination] = report.BytesReceived;
                inFlightCost[item.Destination] = Math.Min(report.BytesReceived, item.Cost);
                ReportProgress(item.DisplayName, force: false);
            });

            var result = await client.DownloadFileAsync(
                item.Urls,
                item.Destination,
                item.Sha1,
                item.Size > 0 ? item.Size : null,
                fileProgress,
                token);

            lock (progressGate)
            {
                inFlightBytes.TryRemove(item.Destination, out _);
                inFlightCost.TryRemove(item.Destination, out _);
                completedBytes += result.Skipped ? item.Size : result.BytesWritten;
                completedCost += item.Cost;
                completedFiles++;
            }

            ReportProgress(item.DisplayName, force: false);
        }

        // 尾段主要是大量 assets 小文件，受请求延迟影响；给它们更高的并发。
        // 两者并行执行，避免所有小文件都等到最后才开始。
        const long smallFileThreshold = 1024 * 1024;
        var largeFiles = work
            .Where(item => item.Size >= smallFileThreshold)
            .OrderByDescending(item => item.Size)
            .ToList();
        var smallFiles = work
            .Where(item => item.Size < smallFileThreshold)
            .OrderByDescending(item => item.Size)
            .ToList();

        using var poolCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var largeTask = Parallel.ForEachAsync(
            largeFiles,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism),
                CancellationToken = poolCts.Token,
            },
            DownloadItemAsync);

        var smallTask = Parallel.ForEachAsync(
            smallFiles,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.SmallFileMaxDegreeOfParallelism),
                CancellationToken = poolCts.Token,
            },
            DownloadItemAsync);

        // 任一池失败时立即取消另一池，避免一个坏文件让整批下载跑到结束才报错。
        _ = CancelPoolOnFaultAsync(largeTask, poolCts);
        _ = CancelPoolOnFaultAsync(smallTask, poolCts);

        await Task.WhenAll(largeTask, smallTask);

        // 5. 最后写入版本 JSON，避免中断时留下不完整版本
        stage = MinecraftDownloadStage.Finalizing;
        WriteFile(Path.Combine(versionDirectory, versionId + ".json"), versionJsonBytes);

        lock (progressGate)
        {
            completedFiles++;
            completedBytes += versionJsonBytes.LongLength;
            completedCost += Math.Max(versionJsonBytes.LongLength, MinimumFileCost);
        }

        ReportProgress("版本 JSON", force: true);

        // 6. 版本隔离设置（PCL2 兼容格式）
        IniFile.SetValue(
            Path.Combine(versionDirectory, "PCL", "Setup.ini"),
            "VersionArgumentIndieV2",
            isolate ? "True" : "False");

        return new MinecraftVersionDownloadResult(
            versionId,
            versionDirectory,
            totalFiles,
            totalBytes,
            isolate);
    }


    /// <summary>
    /// 观察一个下载池；它因异常结束时取消另一个池。
    /// 取消（用户中断或对侧失败）不重复处理。
    /// </summary>
    private static async Task CancelPoolOnFaultAsync(Task poolTask, CancellationTokenSource poolCts)
    {
        try
        {
            await poolTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            try
            {
                poolCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 下载已结束并释放了令牌源。
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

    private static DownloadArtifact? SelectNativeArtifact(
        IReadOnlyDictionary<string, string> natives,
        IReadOnlyDictionary<string, DownloadArtifact> classifiers,
        LaunchEnvironment environment)
    {
        if (!natives.TryGetValue(environment.OsName, out var template) || string.IsNullOrEmpty(template))
            return null;

        var archToken = environment.Arch switch
        {
            "x86_64" => "64",
            "x86" => "32",
            "arm64" => "arm64",
            _ => "64",
        };

        if (classifiers.TryGetValue(template.Replace("${arch}", archToken), out var artifact))
            return artifact;

        if (classifiers.TryGetValue(template.Replace("${arch}", string.Empty), out artifact))
            return artifact;

        return null;
    }

    private static string Normalize(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    private static string EnsureTrailingSlash(string url)
        => url.EndsWith('/') ? url : url + "/";

    private static void WriteFile(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>
    /// 读取本地已有的元数据，只有哈希（以及已知的大小）完全匹配时才返回。
    /// 没有哈希的条目不做复用，避免信任损坏或手工修改过的文件。
    /// </summary>
    private static byte[]? TryReadVerified(string path, string? expectedSha1, long? expectedSize)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(expectedSha1) || !File.Exists(path))
                return null;

            if (expectedSize is > 0 && new FileInfo(path).Length != expectedSize.Value)
                return null;

            var bytes = File.ReadAllBytes(path);
            return DownloadClient.HashMatches(bytes, expectedSha1) ? bytes : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed record DownloadWork(
        string DisplayName,
        string Destination,
        IReadOnlyList<string> Urls,
        string? Sha1,
        long Size)
    {
        /// <summary>进度成本：真实字节数 + 一次请求的固定开销。</summary>
        public long Cost => Math.Max(Size, MinimumFileCost);
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}

public sealed class AssetIndexManifest
{
    [JsonPropertyName("objects")] public Dictionary<string, AssetObject> Objects { get; set; } = new();
}

public sealed class AssetObject
{
    [JsonPropertyName("hash")] public string? Hash { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
}
