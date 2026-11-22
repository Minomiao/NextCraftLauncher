using System.Net;
using System.Net.Sockets;

namespace NextCraftLauncher.Core.Net;

/// <summary>
/// HTTP 连接策略：优先尝试 IPv6，连不通时快速回退到 IPv4。
/// </summary>
/// <remarks>
/// 系统默认会按地址逐个尝试并等满 <c>ConnectTimeout</c>。当 DNS 返回了 AAAA 记录、
/// 但本机 IPv6 实际不可达时，请求会长时间没有任何进展（下载进度一直为 0）。
/// 这里给每个地址错开一点时间并行尝试，保留 IPv6 的优先级，又不会在坏掉的
/// IPv6 上一直等；哪个先连上就用哪个。
/// IPv4 与 IPv6 的带宽本身没有差距，差异都在线路和路由上。
/// </remarks>
public static class HappyEyeballs
{
    /// <summary>前一个地址还没连上时，多久后开始尝试下一个地址。</summary>
    private static readonly TimeSpan FallbackDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>把连接策略装到 <see cref="SocketsHttpHandler"/> 上。</summary>
    public static SocketsHttpHandler Configure(SocketsHttpHandler handler)
    {
        handler.ConnectCallback = ConnectAsync;
        return handler;
    }

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        if (addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        // IPv6 优先，IPv4 兜底；同族地址保持 DNS 返回的顺序。
        var ordered = addresses
            .OrderByDescending(address => address.AddressFamily == AddressFamily.InterNetworkV6)
            .ToArray();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var attempts = new List<Task<Socket>>(ordered.Length);

        for (var index = 0; index < ordered.Length; index++)
        {
            attempts.Add(ConnectOneAsync(ordered[index], context.DnsEndPoint.Port, index, linked.Token));
        }

        // 落败的分支会抛异常；提前观察掉，避免未观察异常。
        foreach (var attempt in attempts)
        {
            _ = attempt.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        Exception? lastError = null;

        try
        {
            while (attempts.Count > 0)
            {
                var finished = await Task.WhenAny(attempts);
                attempts.Remove(finished);

                if (finished.IsCompletedSuccessfully)
                {
                    // 关掉仍在尝试的分支；已经建立的连接不受影响。
                    linked.Cancel();
                    return new NetworkStream(finished.Result, ownsSocket: true);
                }

                try
                {
                    await finished;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    lastError = exception;
                }
            }
        }
        finally
        {
            linked.Cancel();
        }

        throw lastError ?? new SocketException((int)SocketError.HostUnreachable);
    }

    private static async Task<Socket> ConnectOneAsync(
        IPAddress address,
        int port,
        int index,
        CancellationToken cancellationToken)
    {
        if (index > 0)
            await Task.Delay(FallbackDelay * index, cancellationToken);

        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
