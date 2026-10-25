using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using NextCraftLauncher.Core.Settings;

namespace NextCraftLauncher.Download.Java;

public sealed class JavaRuntimeDownloadOptions
{
    public DownloadMirror Mirror { get; init; } = DownloadMirror.Automatic;

    public int MaxDegreeOfParallelism { get; init; } = 8;

    /// <summary>可注入自定义下载器（测试或代理场景）；为空时内部创建。</summary>
    public DownloadClient? Client { get; init; }

    /// <summary>仅用于测试或自定义镜像源；默认使用 Mojang 官方清单。</summary>
    public string? RuntimeIndexUrl { get; init; }
}

public sealed record JavaRuntimeDownloadProgress(
    int CompletedFiles,
    int TotalFiles,
    string? CurrentFile,
    long BytesDownloaded,
    long? TotalBytes)
{
    public double? Percent => TotalBytes is > 0
        ? (double)BytesDownloaded / TotalBytes.Value * 100
        : TotalFiles > 0
            ? (double)CompletedFiles / TotalFiles * 100
            : null;
}

public sealed record JavaRuntimeDownloadResult(
    string ComponentName,
    string RuntimeDirectory,
    string JavaExecutablePath,
    int MajorVersion,
    string VersionName);

/// <summary>
/// 按 Mojang 的 java-runtime 清单下载完整的 Java 运行时，布局与官方启动器/PCL2 一致：
/// .minecraft/runtime/&lt;component&gt;/...
/// </summary>
public static class JavaRuntimeDownloader
{
    public const string OfficialIndexUrl =
        "https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<JavaRuntimeDownloadResult> DownloadAsync(
        string minecraftDirectory,
        string? preferredComponent,
        int? requiredMajor,
        IProgress<JavaRuntimeDownloadProgress>? progress = null,
        JavaRuntimeDownloadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new JavaRuntimeDownloadOptions();
        var client = options.Client ?? DownloadClient.Shared;

        var indexUrl = options.RuntimeIndexUrl ?? OfficialIndexUrl;
        var indexBytes = await client.DownloadBytesAutoAsync(
            DownloadUrlResolver.Build(indexUrl, options.Mirror),
            options.Mirror,
            cancellationToken: cancellationToken);

        var index = JsonSerializer.Deserialize<
                        Dictionary<string, Dictionary<string, List<JavaRuntimeIndexEntry>>>>(
                        indexBytes, JsonOptions)
                    ?? throw new InvalidDataException("无法解析 Java 运行时清单。");

        var platformKey = JavaRuntimePlatform.GetKey();
        if (!index.TryGetValue(platformKey, out var components) || components.Count == 0)
            throw new InvalidOperationException($"Java 运行时清单中没有平台 {platformKey} 的数据。");

        var selection = SelectComponent(components, preferredComponent, requiredMajor)
                        ?? throw new InvalidOperationException(
                            $"未找到适合 Java {requiredMajor?.ToString() ?? "?"} 的运行时（组件：{preferredComponent ?? "自动"}）。");

        var (componentName, entry) = selection;
        var manifestReference = entry.Manifest
                                ?? throw new InvalidDataException($"组件 {componentName} 缺少运行时清单。");
        if (string.IsNullOrWhiteSpace(manifestReference.Url))
            throw new InvalidDataException($"组件 {componentName} 的运行时清单地址为空。");

        var manifestBytes = await client.DownloadBytesAutoAsync(
            DownloadUrlResolver.Build(manifestReference.Url, options.Mirror),
            options.Mirror,
            string.IsNullOrWhiteSpace(manifestReference.Sha1) ? null : manifestReference.Sha1,
            manifestReference.Size > 0 ? manifestReference.Size : null,
            cancellationToken);

        var manifest = JsonSerializer.Deserialize<JavaRuntimeManifest>(manifestBytes, JsonOptions)
                       ?? throw new InvalidDataException("无法解析 Java 运行时文件清单。");

        var runtimeDirectory = Path.Combine(minecraftDirectory, "runtime", componentName);
        Directory.CreateDirectory(runtimeDirectory);

        var files = new List<RuntimeFileWork>();
        long totalBytes = 0;

        foreach (var (relativePath, file) in manifest.Files)
        {
            var normalizedPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
            var destination = GetSafeDestination(runtimeDirectory, normalizedPath);

            if (string.Equals(file.Type, "directory", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            if (string.Equals(file.Type, "link", StringComparison.OrdinalIgnoreCase))
            {
                CreateSymbolicLink(destination, file.Target);
                continue;
            }

            if (file.Downloads?.Raw is not { Url: { Length: > 0 } url } raw) continue;

            files.Add(new RuntimeFileWork(
                relativePath,
                destination,
                url,
                string.IsNullOrWhiteSpace(raw.Sha1) ? null : raw.Sha1,
                raw.Size,
                file.Executable));

            if (raw.Size > 0) totalBytes += raw.Size;
        }

        var completedFiles = 0;
        long completedBytes = 0;
        var inFlightBytes = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        var lastReportTicks = 0L;
        var reportGate = new object();

        void ReportProgress(string? currentFile, bool force)
        {
            var now = Environment.TickCount64;
            lock (reportGate)
            {
                if (!force && now - lastReportTicks < 200) return;
                lastReportTicks = now;
            }

            var bytes = Interlocked.Read(ref completedBytes);
            foreach (var value in inFlightBytes.Values) bytes += value;

            progress?.Report(new JavaRuntimeDownloadProgress(
                Volatile.Read(ref completedFiles),
                files.Count,
                currentFile,
                bytes,
                totalBytes > 0 ? totalBytes : null));
        }

        await Parallel.ForEachAsync(
            files,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism),
                CancellationToken = cancellationToken,
            },
            async (work, token) =>
            {
                var fileProgress = new InlineProgress<DownloadProgress>(report =>
                {
                    inFlightBytes[work.RelativePath] = report.BytesReceived;
                    ReportProgress(work.RelativePath, force: false);
                });

                var result = await client.DownloadFileAsync(
                    DownloadUrlResolver.Build(work.Url, options.Mirror),
                    work.Destination,
                    work.Sha1,
                    work.Size > 0 ? work.Size : null,
                    fileProgress,
                    token);

                if (work.Executable && !OperatingSystem.IsWindows())
                    MarkExecutable(work.Destination);

                inFlightBytes.TryRemove(work.RelativePath, out _);
                Interlocked.Add(ref completedBytes, result.Skipped ? work.Size : result.BytesWritten);
                Interlocked.Increment(ref completedFiles);
                ReportProgress(work.RelativePath, force: false);
            });

        var javaExecutable = Path.Combine(
            runtimeDirectory,
            "bin",
            OperatingSystem.IsWindows() ? "java.exe" : "java");

        if (!File.Exists(javaExecutable))
            throw new FileNotFoundException("Java 运行时下载完成，但没有找到 bin/java。", javaExecutable);

        var versionName = entry.Version?.Name ?? string.Empty;
        var majorVersion = ParseMajor(versionName);
        if (majorVersion <= 0) majorVersion = requiredMajor ?? 0;

        return new JavaRuntimeDownloadResult(
            componentName,
            runtimeDirectory,
            javaExecutable,
            majorVersion,
            versionName);
    }

    private static (string ComponentName, JavaRuntimeIndexEntry Entry)? SelectComponent(
        IReadOnlyDictionary<string, List<JavaRuntimeIndexEntry>> components,
        string? preferredComponent,
        int? requiredMajor)
    {
        if (!string.IsNullOrWhiteSpace(preferredComponent) &&
            components.TryGetValue(preferredComponent.Trim(), out var preferredEntries))
        {
            var preferred = preferredEntries.FirstOrDefault(entry => entry.Manifest?.Url is { Length: > 0 });
            if (preferred is not null) return (preferredComponent.Trim(), preferred);
        }

        if (requiredMajor is not int major) return null;

        var candidates = new List<(string Name, JavaRuntimeIndexEntry Entry)>();

        foreach (var (name, entries) in components)
        {
            // minecraft-java-exe 不是 JRE。
            if (string.Equals(name, "minecraft-java-exe", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var entry in entries)
            {
                if (entry.Manifest?.Url is not { Length: > 0 }) continue;
                if (ParseMajor(entry.Version?.Name) != major) continue;
                candidates.Add((name, entry));
            }
        }

        if (candidates.Count == 0) return null;

        var match = candidates
            .OrderBy(candidate => candidate.Name.StartsWith("jre-legacy", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .First();

        return (match.Name, match.Entry);
    }


    private static string GetSafeDestination(string runtimeDirectory, string relativePath)
    {
        var root = Path.GetFullPath(runtimeDirectory);
        var destination = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"运行时清单包含非法路径：{relativePath}");

        return destination;
    }

    private static void CreateSymbolicLink(string destination, string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;

        try
        {
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            if (File.Exists(destination) || Directory.Exists(destination)) return;

            File.CreateSymbolicLink(destination, target);
        }
        catch (Exception)
        {
            // Windows 上创建符号链接可能需要权限；Windows 清单通常没有 link 项。
        }
    }

    private static void MarkExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (Exception)
        {
            // 非 Unix 平台或不支持时忽略。
        }
    }

    private static int ParseMajor(string? versionName)
    {
        if (string.IsNullOrWhiteSpace(versionName)) return 0;
        var match = Regex.Match(versionName, @"\d+");
        return match.Success && int.TryParse(match.Value, out var major) ? major : 0;
    }

    private sealed record RuntimeFileWork(
        string RelativePath,
        string Destination,
        string Url,
        string? Sha1,
        long Size,
        bool Executable);

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
