using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using NextCraftLauncher.Core.Settings;

namespace NextCraftLauncher.Download;

/// <summary>
/// 支持镜像地址、重试、断点续传和 SHA-1 校验的下载器。
/// 单个实例可并发使用。
/// </summary>
/// <remarks>
/// 针对国内网络做了五点优化：
/// 1. 候选地址轮转：一个源失败后立刻换下一个源，而不是在同一源上重试三次；
/// 2. 停滞超时：连接或读取超过 <see cref="IdleTimeout"/> 秒没有进展就放弃当前地址，
///    避免慢速源把下载卡住；清单类小文件使用更短的 <see cref="MetadataIdleTimeout"/>；
/// 3. 源健康度记忆：被限流（429/503 等）的源在本次下载内直接跳过，全部改走备用源；
/// 4. 对冲竞速：「自动」模式下官方立即请求、BMCLAPI 延后加入，谁先完整返回用谁；
/// 5. 共享实例：<see cref="Shared"/> 复用连接池与 DNS 结果，避免每类操作重新建连。
/// </remarks>
public sealed class DownloadClient : IDisposable
{
    private const int MaxAttempts = 3;
    private const int BufferSize = 81920;

    /// <summary>连续超过该时间没有收到任何数据，视为连接停滞。</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 清单、版本 JSON、资源索引等元数据的停滞超时。
    /// 这些文件只有几百 KB，没必要沿用大文件的 20 秒等待，否则一个卡住的源就会拖住整个流程。
    /// </summary>
    private static readonly TimeSpan MetadataIdleTimeout = TimeSpan.FromSeconds(8);

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 「自动」模式下镜像源的加入延迟：官方请求立即发出，镜像只作为对冲，
    /// 不必等官方彻底失败，也不会在官方正常时抢跑。
    /// </summary>
    private static readonly TimeSpan MirrorHedgeDelay = TimeSpan.FromMilliseconds(800);

    /// <summary>同一路径前缀连续失败多少次后跳过该源。</summary>
    private const int PrefixFailureThreshold = 3;

    /// <summary>平均速度低于该值时，如果还有备选源则切换。</summary>
    private const long MinSpeedBytesPerSecond = 512 * 1024;

    /// <summary>尚未测到高速文件时的速度下限，避免慢速宽带上误判。</summary>
    private const long InitialMinSpeedBytesPerSecond = 64 * 1024;

    /// <summary>本次下载中已观测到的高速阈值，超过后启用严格低速判断。</summary>
    private const long BestSpeedThreshold = 2 * 1024 * 1024;

    /// <summary>高速观测值的有效期。共享实例下不能用永久最大值，否则后续下载会误判为低速。</summary>
    private static readonly TimeSpan FastSpeedMemory = TimeSpan.FromMinutes(3);

    /// <summary>普通失败计数的有效期。共享实例下必须过期，否则一次网络抖动会永久拉黑某个源。</summary>
    private static readonly TimeSpan FailureMemory = TimeSpan.FromMinutes(5);

    /// <summary>低速检测的宽限时间，避免把正常的慢启动误判为慢速。</summary>
    private static readonly TimeSpan SpeedCheckGrace = TimeSpan.FromSeconds(3);

    /// <summary>被限流后跳过该源的时长。</summary>
    private static readonly TimeSpan ThrottleCooldown = TimeSpan.FromMinutes(5);

    /// <summary>因速度过低而跳过的时长。</summary>
    private static readonly TimeSpan SlowCooldown = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 应用级共享实例：复用连接池、DNS 结果和源健康度记忆。
    /// 该实例随进程存活，调用方不要释放。
    /// </summary>
    public static DownloadClient Shared { get; } = new();

    private long _bestFileBytesPerSecond;
    private long _bestFileSpeedTicks;

    private readonly HttpClient _httpClient;

    /// <summary>
    /// 元数据专用客户端：开启 gzip/brotli 解压。
    /// 文件下载仍用 <see cref="_httpClient"/>（不压缩），避免压缩后的 Content-Length
    /// 与断点续传的 Range 语义错位。
    /// </summary>
    private readonly HttpClient _metadataClient;

    private readonly bool _ownsClient;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, long LastTicks)> _failureCounts =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _throttledUntil =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _prefixSpeeds =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>官方 / BMCLAPI 两个源的整体速度印象，用于尚未测速的新路径排序。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _sourceSpeeds =
        new(StringComparer.OrdinalIgnoreCase);

    private int _candidateCounter;

    public DownloadClient(HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;

        if (httpClient is null)
        {
            _httpClient = CreateClient(DecompressionMethods.None);
            _metadataClient = CreateClient(
                DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli);
        }
        else
        {
            _httpClient = httpClient;
            _metadataClient = httpClient;
        }

        // 单文件下载可能很久，用停滞超时控制挂死，而不是整体超时。
        if (_ownsClient)
            _httpClient.Timeout = TimeSpan.FromHours(2);

        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("NextCraftLauncher/0.1.0");

        if (!ReferenceEquals(_metadataClient, _httpClient) &&
            !_metadataClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _metadataClient.DefaultRequestHeaders.UserAgent.ParseAdd("NextCraftLauncher/0.1.0");
        }
    }

    private static HttpClient CreateClient(DecompressionMethods decompression)
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = decompression,
            ConnectTimeout = ConnectTimeout,
            MaxConnectionsPerServer = 64,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        client.Timeout = TimeSpan.FromHours(2);
        return client;
    }

    /// <summary>
    /// 依次尝试 urls，支持从 .part 文件续传。目标文件已存在且校验通过时直接跳过。
    /// </summary>
    public async Task<DownloadResult> DownloadFileAsync(
        IReadOnlyList<string> urls,
        string destinationPath,
        string? expectedSha1 = null,
        long? expectedSize = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (urls.Count == 0) throw new ArgumentException("至少需要一个下载地址。", nameof(urls));
        if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("目标路径不能为空。", nameof(destinationPath));

        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        if (IsValidFile(fullPath, expectedSha1, expectedSize))
        {
            var length = new FileInfo(fullPath).Length;
            return new DownloadResult(fullPath, length, Skipped: true, HashVerified: expectedSha1 is not null);
        }

        var partPath = fullPath + ".part";
        var candidates = OrderCandidates(urls);
        var hasAlternatives = candidates.Length > 1;
        Exception? lastError = null;

        for (var round = 0; round < MaxAttempts; round++)
        {
            foreach (var url in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    return await DownloadFromUrlAsync(
                        url, fullPath, partPath, expectedSha1, expectedSize, progress, hasAlternatives, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    lastError = exception;
                    RecordFailure(url, exception);
                }
            }

            if (round < MaxAttempts - 1)
                await Task.Delay(TimeSpan.FromMilliseconds(500 * (1 << round)), cancellationToken);
        }

        throw new IOException($"下载失败：{destinationPath}（{lastError?.Message}）", lastError);
    }

    /// <summary>
    /// 下载到内存（用于清单等小文件），同样支持镜像与重试。
    /// </summary>
    public async Task<byte[]> DownloadBytesAsync(
        IReadOnlyList<string> urls,
        string? expectedSha1 = null,
        long? expectedSize = null,
        CancellationToken cancellationToken = default)
    {
        if (urls.Count == 0) throw new ArgumentException("至少需要一个下载地址。", nameof(urls));

        var candidates = OrderCandidates(urls);
        Exception? lastError = null;

        for (var round = 0; round < MaxAttempts; round++)
        {
            foreach (var url in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    return await DownloadBytesFromUrlAsync(url, expectedSha1, expectedSize, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    lastError = exception;
                    RecordFailure(url, exception);
                }
            }

            if (round < MaxAttempts - 1)
                await Task.Delay(TimeSpan.FromMilliseconds(500 * (1 << round)), cancellationToken);
        }

        throw new IOException($"下载失败：{candidates[0]}（{lastError?.Message}）", lastError);
    }

    /// <summary>
    /// 对冲竞速：首选源立即请求，「自动」模式下的 BMCLAPI 延后
    /// <see cref="MirrorHedgeDelay"/> 再加入。
    /// </summary>
    /// <remarks>
    /// 胜出条件是完整读完响应体，而不是先收到响应头。
    /// BMCLAPI 会 302 到随机节点，只比响应头会让卡在重定向目标上的请求假胜出，
    /// 反而比单源更慢。
    /// </remarks>
    public async Task<byte[]> DownloadBytesHedgedAsync(
        IReadOnlyList<string> urls,
        DownloadMirror mirror,
        string? expectedSha1 = null,
        long? expectedSize = null,
        CancellationToken cancellationToken = default)
    {
        var candidates = urls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (candidates.Length == 0) throw new ArgumentException("至少需要一个下载地址。", nameof(urls));
        if (candidates.Length == 1)
            return await DownloadBytesFromUrlAsync(candidates[0], expectedSha1, expectedSize, cancellationToken);

        var hasPrimary = candidates.Any(url => !IsBmclApi(url));
        var cancels = candidates
            .Select(_ => CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            .ToArray();

        var attempts = candidates
            .Select((url, index) => DownloadBytesAfterDelayAsync(
                url,
                GetHedgeDelay(url, mirror, hasPrimary),
                expectedSha1,
                expectedSize,
                cancels[index].Token))
            .ToArray();

        // 每个分支结束后自行释放令牌源并观察异常。
        // 不能在这里 await 落败分支：DNS 等阶段不一定能立即取消，
        // 等待它们会抵消对冲竞速带来的收益。
        for (var i = 0; i < attempts.Length; i++)
        {
            var cts = cancels[i];
            _ = attempts[i].ContinueWith(
                static (task, state) =>
                {
                    _ = task.Exception;
                    ((CancellationTokenSource)state!).Dispose();
                },
                cts,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        var remaining = attempts.ToList();
        byte[]? result = null;
        Exception? lastError = null;
        var lastErrorUrl = candidates[0];

        try
        {
            while (remaining.Count > 0)
            {
                var finished = await Task.WhenAny(remaining);
                remaining.Remove(finished);

                try
                {
                    result = await finished;
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    lastError = exception;

                    var index = Array.IndexOf(attempts, finished);
                    if (index >= 0) lastErrorUrl = candidates[index];
                }
            }
        }
        finally
        {
            foreach (var cts in cancels)
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 分支已完成并自行释放。
                }
            }
        }

        if (result is not null) return result;

        throw new IOException($"下载失败：{lastErrorUrl}（{lastError?.Message}）", lastError);
    }

    private async Task<byte[]> DownloadBytesAfterDelayAsync(
        string url,
        TimeSpan delay,
        string? expectedSha1,
        long? expectedSize,
        CancellationToken cancellationToken)
    {
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken);

        try
        {
            return await DownloadBytesFromUrlAsync(url, expectedSha1, expectedSize, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            RecordFailure(url, exception);
            throw;
        }
    }

    private static TimeSpan GetHedgeDelay(string url, DownloadMirror mirror, bool hasPrimary)
        => mirror == DownloadMirror.Automatic && hasPrimary && IsBmclApi(url)
            ? MirrorHedgeDelay
            : TimeSpan.Zero;

    /// <summary>
    /// 小文件下载入口：显式选择 BMCLAPI 时按候选顺序回退；
    /// 其余模式（含「自动」）使用对冲竞速——首选源立即发出，BMCLAPI 延后加入。
    /// </summary>
    public async Task<byte[]> DownloadBytesAutoAsync(
        IReadOnlyList<string> urls,
        DownloadMirror mirror,
        string? expectedSha1 = null,
        long? expectedSize = null,
        CancellationToken cancellationToken = default)
    {
        var candidates = urls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (candidates.Length == 0) throw new ArgumentException("至少需要一个下载地址。", nameof(urls));

        if (mirror == DownloadMirror.BmclApi)
            return await DownloadBytesAsync(candidates, expectedSha1, expectedSize, cancellationToken);

        return await DownloadBytesHedgedAsync(candidates, mirror, expectedSha1, expectedSize, cancellationToken);
    }

    private static bool IsBmclApi(string url)
        => url.Contains("bmclapi", StringComparison.OrdinalIgnoreCase);

    public static string ComputeSha1(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
    }

    public static bool HashMatches(string path, string expectedSha1)
    {
        return string.Equals(ComputeSha1(path), expectedSha1, StringComparison.OrdinalIgnoreCase);
    }

    public static bool HashMatches(byte[] content, string expectedSha1)
    {
        return string.Equals(
            Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant(),
            expectedSha1,
            StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (!_ownsClient) return;

        _httpClient.Dispose();
        _metadataClient.Dispose();
    }

    /// <summary>
    /// 过滤掉正在限流/连续失败的源，并按实测速度排序；定期试探未测速的源。
    /// </summary>
    private string[] OrderCandidates(IReadOnlyList<string> urls)
    {
        var all = urls.Where(url => !string.IsNullOrWhiteSpace(url)).ToArray();
        if (all.Length == 0) throw new ArgumentException("至少需要一个有效的下载地址。", nameof(urls));

        var available = all.Where(url => !IsUnavailable(url)).ToArray();
        var candidates = available.Length > 0 ? available : all;

        if (candidates.Length <= 1) return candidates;

        // 每 16 个文件抽一个去试未测速的源，避免“一直用第一个源”而永远不知道另一个源更快。
        if (Interlocked.Increment(ref _candidateCounter) % 16 == 0)
        {
            var unmeasured = candidates.FirstOrDefault(url => GetSpeed(url) is null);
            if (unmeasured is not null)
            {
                candidates = new[] { unmeasured }
                    .Concat(candidates.Where(url => !string.Equals(url, unmeasured, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
            }
        }

        return candidates
            .Select((url, index) => (url, index, speed: GetSpeed(url), source: GetSourceSpeed(url)))
            .OrderByDescending(item => item.speed ?? -1)
            .ThenByDescending(item => item.source)
            .ThenBy(item => item.index)
            .Select(item => item.url)
            .ToArray();
    }

    private double? GetSpeed(string url)
        => GetPrefix(url) is { } prefix && _prefixSpeeds.TryGetValue(prefix, out var speed)
            ? speed
            : null;

    private void RecordSpeed(string url, double bytesPerSecond)
    {
        var prefix = GetPrefix(url);
        if (prefix is null || bytesPerSecond <= 0) return;

        _prefixSpeeds.AddOrUpdate(
            prefix,
            bytesPerSecond,
            (_, current) => current <= 0 ? bytesPerSecond : current * 0.7 + bytesPerSecond * 0.3);

        RecordSourceSpeed(url, bytesPerSecond);
    }

    private void RecordSourceSpeed(string url, double bytesPerSecond)
    {
        var key = GetSourceKey(url);
        if (key is null || bytesPerSecond <= 0) return;

        _sourceSpeeds.AddOrUpdate(
            key,
            bytesPerSecond,
            (_, current) => current <= 0 ? bytesPerSecond : current * 0.7 + bytesPerSecond * 0.3);
    }

    private double GetSourceSpeed(string url)
    {
        var key = GetSourceKey(url);
        return key is not null && _sourceSpeeds.TryGetValue(key, out var speed) ? speed : -1;
    }

    private static string? GetSourceKey(string url)
    {
        if (url.Contains("bmclapi", StringComparison.OrdinalIgnoreCase)) return "bmcl";
        if (url.Contains("mojang.com", StringComparison.OrdinalIgnoreCase)) return "official";
        return null;
    }

    private bool IsUnavailable(string url)
    {
        var prefix = GetPrefix(url);
        if (prefix is null) return false;

        if (_throttledUntil.TryGetValue(prefix, out var until) && Environment.TickCount64 < until)
            return true;

        return _failureCounts.TryGetValue(prefix, out var failure)
               && failure.Count >= PrefixFailureThreshold
               && Environment.TickCount64 - failure.LastTicks < (long)FailureMemory.TotalMilliseconds;
    }

    private void RecordFailure(string url, Exception exception)
    {
        var prefix = GetPrefix(url);
        if (prefix is null) return;

        if (exception is SlowDownloadException)
        {
            // 不换源会一直慢慢拉住，先冷却一段时间，让后续文件直接走备用源。
            _throttledUntil[prefix] = Environment.TickCount64 + (long)SlowCooldown.TotalMilliseconds;
            return;
        }

        if (IsThrottling(exception))
        {
            // 429 / 503 说明该节点整体不可用，直接冷却，后续文件不再尝试。
            _throttledUntil[prefix] = Environment.TickCount64 + (long)ThrottleCooldown.TotalMilliseconds;
            _failureCounts[prefix] = (PrefixFailureThreshold, Environment.TickCount64);
            return;
        }

        _failureCounts.AddOrUpdate(
            prefix,
            static _ => (1, Environment.TickCount64),
            static (_, current) => (current.Count + 1, Environment.TickCount64));
    }

    private static bool IsThrottling(Exception exception)
        => exception is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable }
           || exception is HttpRequestException { StatusCode: HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout };

    private static string? GetPrefix(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        var firstSegment = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        return firstSegment is null
            ? uri.Host
            : $"{uri.Host}/{firstSegment}";
    }

    private async Task<DownloadResult> DownloadFromUrlAsync(
        string url,
        string destinationPath,
        string partPath,
        string? expectedSha1,
        long? expectedSize,
        IProgress<DownloadProgress>? progress,
        bool hasAlternatives,
        CancellationToken cancellationToken)
    {
        var existingLength = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        if (expectedSize is long expected && existingLength > expected)
        {
            File.Delete(partPath);
            existingLength = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existingLength > 0)
            request.Headers.Range = new RangeHeaderValue(existingLength, null);

        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        HttpResponseMessage response;
        try
        {
            idle.CancelAfter(IdleTimeout);
            response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, idle.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"连接 {url} 超时（{IdleTimeout.TotalSeconds:0} 秒无响应）。");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                if (File.Exists(partPath)) File.Delete(partPath);
                throw new HttpRequestException("服务器拒绝了断点续传，将重新下载。");
            }

            response.EnsureSuccessStatusCode();

            var append = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            var startOffset = append ? existingLength : 0;

            long? totalBytes = expectedSize;
            if (totalBytes is null && response.Content.Headers.ContentLength is long remainingBytes)
                totalBytes = startOffset + remainingBytes;

            long written = startOffset;
            var speedClock = Stopwatch.StartNew();

            await using (var fileStream = new FileStream(
                             partPath,
                             append ? FileMode.Append : FileMode.Create,
                             FileAccess.Write,
                             FileShare.Read,
                             BufferSize,
                             useAsync: true))
            await using (var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                var buffer = new byte[BufferSize];
                while (true)
                {
                    int read;
                    try
                    {
                        idle.CancelAfter(IdleTimeout);
                        read = await responseStream.ReadAsync(buffer, idle.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException($"下载 {url} 停滞超过 {IdleTimeout.TotalSeconds:0} 秒。");
                    }

                    if (read == 0) break;

                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    written += read;

                    // 大文件如果平均速度长期低于阈值，且还有备用源，则立刻换源续传。
                    if (hasAlternatives && expectedSize is >= 512 * 1024 &&
                        speedClock.Elapsed >= SpeedCheckGrace &&
                        expectedSize - written > 256 * 1024)
                    {
                        var transferred = written - startOffset;
                        var bytesPerSecond = transferred / speedClock.Elapsed.TotalSeconds;

                        // 先看最近是否出现过高速文件：有则用严格阈值，
                        // 没有则用小阈值，避免在慢速宽带上误判。
                        var best = Interlocked.Read(ref _bestFileBytesPerSecond);
                        var bestTicks = Interlocked.Read(ref _bestFileSpeedTicks);
                        var recentlyFast = best >= BestSpeedThreshold &&
                                           Environment.TickCount64 - bestTicks <=
                                           (long)FastSpeedMemory.TotalMilliseconds;
                        var threshold = recentlyFast
                            ? MinSpeedBytesPerSecond
                            : InitialMinSpeedBytesPerSecond;

                        if (bytesPerSecond < threshold)
                            throw new SlowDownloadException(url);
                    }

                    progress?.Report(new DownloadProgress(
                        Path.GetFileName(destinationPath), written, totalBytes));
                }
            }

            if (expectedSize is long size && new FileInfo(partPath).Length != size)
            {
                throw new InvalidDataException($"文件大小不匹配：期望 {size}，实际 {new FileInfo(partPath).Length}。");
            }

            var transferredBytes = written - startOffset;
            if (transferredBytes >= 512 * 1024)
            {
                UpdateBestSpeed((long)(transferredBytes / Math.Max(speedClock.Elapsed.TotalSeconds, 0.001)));
            }

            if (transferredBytes >= 256 * 1024)
            {
                RecordSpeed(url, transferredBytes / Math.Max(speedClock.Elapsed.TotalSeconds, 0.001));
            }

            if (expectedSha1 is not null && !HashMatches(partPath, expectedSha1))
            {
                File.Delete(partPath);
                throw new InvalidDataException("SHA-1 校验失败。");
            }

            File.Move(partPath, destinationPath, overwrite: true);
            return new DownloadResult(destinationPath, written, Skipped: false, HashVerified: expectedSha1 is not null);
        }
    }

    private async Task<byte[]> DownloadBytesFromUrlAsync(
        string url,
        string? expectedSha1,
        long? expectedSize,
        CancellationToken cancellationToken)
    {
        using var response = await SendBytesRequestAsync(url, cancellationToken);
        return await ReadResponseBytesAsync(response, url, expectedSha1, expectedSize, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendBytesRequestAsync(string url, CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        HttpResponseMessage response;
        try
        {
            idle.CancelAfter(MetadataIdleTimeout);
            response = await _metadataClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, idle.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"连接 {url} 超时（{MetadataIdleTimeout.TotalSeconds:0} 秒无响应）。");
        }

        try
        {
            response.EnsureSuccessStatusCode();
            return response;
        }
        catch (Exception)
        {
            response.Dispose();
            throw;
        }
    }

    private async Task<byte[]> ReadResponseBytesAsync(
        HttpResponseMessage response,
        string url,
        string? expectedSha1,
        long? expectedSize,
        CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var buffer = new MemoryStream();

        await using (var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            var chunk = new byte[BufferSize];
            while (true)
            {
                int read;
                try
                {
                    idle.CancelAfter(MetadataIdleTimeout);
                    read = await responseStream.ReadAsync(chunk, idle.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"读取 {url} 停滞超过 {MetadataIdleTimeout.TotalSeconds:0} 秒。");
                }

                if (read == 0) break;

                buffer.Write(chunk, 0, read);
            }
        }

        var bytes = buffer.ToArray();

        if (expectedSize is long size && bytes.LongLength != size)
            throw new InvalidDataException($"文件大小不匹配：期望 {size}，实际 {bytes.LongLength}。");

        if (expectedSha1 is not null && !HashMatches(bytes, expectedSha1))
            throw new InvalidDataException("SHA-1 校验失败。");

        return bytes;
    }

    private void UpdateBestSpeed(long bytesPerSecond)
    {
        if (bytesPerSecond >= BestSpeedThreshold)
            Interlocked.Exchange(ref _bestFileSpeedTicks, Environment.TickCount64);

        while (true)
        {
            var current = Interlocked.Read(ref _bestFileBytesPerSecond);
            if (bytesPerSecond <= current) return;
            if (Interlocked.CompareExchange(ref _bestFileBytesPerSecond, bytesPerSecond, current) == current)
                return;
        }
    }

    private static bool IsValidFile(string path, string? expectedSha1, long? expectedSize)
    {
        if (!File.Exists(path)) return false;

        if (expectedSize is long size && new FileInfo(path).Length != size) return false;

        if (expectedSha1 is not null) return HashMatches(path, expectedSha1);

        // 只给了大小且大小一致，视为有效。
        return expectedSize is not null;
    }

    private sealed class SlowDownloadException(string url)
        : IOException($"下载速度过低，切换下载源：{url}");
}