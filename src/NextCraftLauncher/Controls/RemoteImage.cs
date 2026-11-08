using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NextCraftLauncher.Core.Logging;
using NextCraftLauncher.Download.Mods;

namespace NextCraftLauncher.Controls;

/// <summary>
/// 远程图片控件：没有图时画一个灰色圆角占位块，进入可视区后（列表虚拟化创建条目时）
/// 才在后台下载并解码，完成后替换。
/// </summary>
public sealed class RemoteImage : Control
{
    public static readonly StyledProperty<IImage?> ImageProperty =
        AvaloniaProperty.Register<RemoteImage, IImage?>(nameof(Image));

    public static readonly StyledProperty<string?> SourceUrlProperty =
        AvaloniaProperty.Register<RemoteImage, string?>(nameof(SourceUrl));

    /// <summary>缓存键（<c>平台/工程 ID</c>），由 <see cref="ModIconCache.MakeKey"/> 生成。</summary>
    public static readonly StyledProperty<string?> CacheKeyProperty =
        AvaloniaProperty.Register<RemoteImage, string?>(nameof(CacheKey));

    private const double CornerRadius = 6;

    private static readonly IBrush PlaceholderBrush =
        new SolidColorBrush(Color.FromArgb(36, 128, 128, 128));

    /// <summary>同一个文件解码失败只记一次日志，避免滚动时刷屏。</summary>
    private static readonly ConcurrentDictionary<string, byte> ReportedFailures = new();

    private CancellationTokenSource? _loadCts;
    private bool _attached;

    static RemoteImage()
    {
        AffectsRender<RemoteImage>(ImageProperty);
        SourceUrlProperty.Changed.AddClassHandler<RemoteImage>((image, _) => image.Restart());
        CacheKeyProperty.Changed.AddClassHandler<RemoteImage>((image, _) => image.Restart());
    }

    public IImage? Image
    {
        get => GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }

    public string? SourceUrl
    {
        get => GetValue(SourceUrlProperty);
        set => SetValue(SourceUrlProperty, value);
    }

    public string? CacheKey
    {
        get => GetValue(CacheKeyProperty);
        set => SetValue(CacheKeyProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        if (Image is not { } image)
        {
            context.DrawRectangle(PlaceholderBrush, null, new RoundedRect(bounds, CornerRadius));
            return;
        }

        context.DrawImage(image, new Rect(image.Size), bounds);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Restart();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        Cancel();
        base.OnDetachedFromVisualTree(e);
    }

    private void Restart()
    {
        Cancel();
        Image = null;

        if (!_attached) return;

        var url = SourceUrl;
        var key = CacheKey;
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key)) return;

        var cts = new CancellationTokenSource();
        _loadCts = cts;
        _ = LoadAsync(key, url, cts.Token);
    }

    private void Cancel()
    {
        var cts = _loadCts;
        _loadCts = null;
        if (cts is null) return;

        cts.Cancel();
        cts.Dispose();
    }

    private async Task LoadAsync(string key, string url, CancellationToken cancellationToken)
    {
        try
        {
            var path = await ModIconCache.EnsureAsync(key, url, cancellationToken);
            if (path is null || cancellationToken.IsCancellationRequested) return;

            var bitmap = await Task.Run(() => Decode(path, cancellationToken), cancellationToken);
            if (bitmap is null) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    bitmap.Dispose();
                    return;
                }

                // 旧图交给 GC：渲染线程可能还引用着它，不能立即释放。
                Image = bitmap;
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            LauncherLog.Warn($"加载 Mod 图标失败（{key}）：{exception.Message}");
        }
    }

    private static Bitmap? Decode(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var stream = File.OpenRead(path);
            return new Bitmap(stream);
        }
        catch (Exception exception)
        {
            if (ReportedFailures.TryAdd(path, 0))
                LauncherLog.Warn($"解码 Mod 图标失败（{Path.GetFileName(path)}）：{exception.Message}");
            return null;
        }
    }
}
