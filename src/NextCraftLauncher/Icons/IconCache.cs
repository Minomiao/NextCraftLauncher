using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NextCraftLauncher.Controls;
using NextCraftLauncher.Core.Logging;
using NextCraftLauncher.Core.Settings;

namespace NextCraftLauncher.Icons;

/// <summary>
/// 固定角度的图标在首次启动时预渲染成 PNG，存到 <c>NCL/cache/icons</c>，
/// 之后 <see cref="BlockIcon"/> 直接把它当图片画，不再每次重建几何。
/// </summary>
public static class IconCache
{
    /// <summary>图标素材或排版有改动时加一，旧缓存会整体重建。</summary>
    private const int CacheVersion = 2;

    /// <summary>预渲染使用的角度，与 <see cref="BlockIcon"/> 的默认值一致。</summary>
    public const double Yaw = 45;
    public const double Pitch = 30;

    /// <summary>预渲染尺寸（显示时再缩放到实际大小）。</summary>
    public const int Size = 256;

    private static readonly string[] Blocks =
    {
        "grass", "dirt", "path", "cobblestone", "gold", "redstone",
        "lamp_on", "lamp_off", "command_block", "anvil", "loom", "blast_furnace",
    };

    private static readonly string[] Sprites = { "fabric", "neoforge", "honeycomb" };

    private static readonly Dictionary<string, Bitmap?> Bitmaps = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    private sealed record Manifest(int Version, int Size, double Yaw, double Pitch);

    /// <summary>预渲染过程中 <see cref="BlockIcon"/> 不要再读缓存，否则会递归。</summary>
    public static bool Rendering { get; private set; }

    public static string RootDirectory => Path.Combine(AppSettings.DataDirectory, "cache", "icons");

    private static string BlocksDirectory => Path.Combine(RootDirectory, "blocks");

    private static string SpritesDirectory => Path.Combine(RootDirectory, "sprites");

    private static string ManifestPath => Path.Combine(RootDirectory, "manifest.json");

    /// <summary>缓存缺失或参数变化时重新渲染；失败只记日志，不影响正常显示。</summary>
    public static void EnsureRendered()
    {
        try
        {
            if (IsCurrent()) return;

            var watch = Stopwatch.StartNew();
            RenderAll();
            watch.Stop();

            LauncherLog.Info(
                $"已预渲染图标 {Blocks.Length + Sprites.Length} 个（{watch.ElapsedMilliseconds} ms）：{RootDirectory}");
        }
        catch (Exception exception)
        {
            LauncherLog.Warn($"预渲染图标失败：{exception.Message}");
        }
    }

    /// <summary>取预渲染好的图标；没有缓存时返回 <c>null</c>，由调用方自己画。</summary>
    public static Bitmap? TryGet(string name)
    {
        if (Rendering || string.IsNullOrWhiteSpace(name)) return null;

        lock (Gate)
        {
            if (Bitmaps.TryGetValue(name, out var cached)) return cached;
        }

        var path = File.Exists(BlockPath(name))
            ? BlockPath(name)
            : File.Exists(SpritePath(name))
                ? SpritePath(name)
                : null;

        Bitmap? bitmap = null;
        if (path is not null)
        {
            try
            {
                using var stream = File.OpenRead(path);
                bitmap = new Bitmap(stream);
            }
            catch (Exception exception)
            {
                LauncherLog.Warn($"读取预渲染图标失败（{name}）：{exception.Message}");
            }
        }

        // 只缓存成功的结果：缓存目录晚于首次绘制生成时，下次能重新读到。
        if (bitmap is not null)
        {
            lock (Gate)
            {
                Bitmaps[name] = bitmap;
            }
        }

        return bitmap;
    }

    private static bool IsCurrent()
    {
        try
        {
            if (!File.Exists(ManifestPath)) return false;

            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestPath));
            if (manifest is not { } value) return false;
            if (value.Version != CacheVersion || value.Size != Size) return false;
            if (Math.Abs(value.Yaw - Yaw) > 0.001 || Math.Abs(value.Pitch - Pitch) > 0.001) return false;

            foreach (var name in Blocks)
            {
                if (!File.Exists(BlockPath(name))) return false;
            }

            foreach (var name in Sprites)
            {
                if (!File.Exists(SpritePath(name))) return false;
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void RenderAll()
    {
        Rendering = true;

        try
        {
            Directory.CreateDirectory(BlocksDirectory);
            Directory.CreateDirectory(SpritesDirectory);

            foreach (var name in Blocks)
            {
                RenderItem(name, BlockPath(name));
            }

            foreach (var name in Sprites)
            {
                RenderItem(name, SpritePath(name));
            }

            var manifest = new Manifest(CacheVersion, Size, Yaw, Pitch);
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest));
        }
        finally
        {
            Rendering = false;

            lock (Gate)
            {
                Bitmaps.Clear();
            }
        }
    }

    private static void RenderItem(string name, string path)
    {
        var icon = new BlockIcon
        {
            Block = name,
            Yaw = Yaw,
            Pitch = Pitch,
            Width = Size,
            Height = Size,
        };

        icon.Measure(new Size(Size, Size));
        icon.Arrange(new Rect(0, 0, Size, Size));

        using var source = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        source.Render(icon);

        var transform = OpticalTransform(source);
        if (transform == Matrix.Identity)
        {
            source.Save(path);
            return;
        }

        var canvas = new BitmapCanvas
        {
            Width = Size,
            Height = Size,
            Source = source,
            Transform = transform,
        };

        canvas.Measure(new Size(Size, Size));
        canvas.Arrange(new Rect(0, 0, Size, Size));

        using var result = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        result.Render(canvas);
        result.Save(path);
    }

    /// <summary>
    /// 用缩小的 alpha 图当“模糊后的轮廓”，算出缩放/平移，
    /// 让图标大致轮廓到左、上、下三边的距离相等。
    /// </summary>
    private static Matrix OpticalTransform(Bitmap bitmap)
    {
        const int grid = 32;
        const float threshold = 0.25f;
        const double fill = 0.92;

        var mask = PerceivedMask(bitmap, grid);
        if (mask is null) return Matrix.Identity;

        var x0 = grid;
        var y0 = grid;
        var x1 = -1;
        var y1 = -1;

        for (var y = 0; y < grid; y++)
        {
            for (var x = 0; x < grid; x++)
            {
                if (mask[x, y] < threshold) continue;
                x0 = Math.Min(x0, x);
                y0 = Math.Min(y0, y);
                x1 = Math.Max(x1, x);
                y1 = Math.Max(y1, y);
            }
        }

        if (x1 < x0 || y1 < y0) return Matrix.Identity;

        var cell = (double)Size / grid;
        var left = x0 * cell;
        var top = y0 * cell;
        var width = (x1 + 1 - x0) * cell;
        var height = (y1 + 1 - y0) * cell;

        // 视觉高度统一缩放，避免不同图标看起来大小不一。
        var scale = Math.Clamp(Size * fill / height, 0.85, 1.25);
        if (width * scale > Size) scale = Size / width;

        var scaledWidth = width * scale;
        var scaledHeight = height * scale;

        var margin = (Size - scaledHeight) / 2;
        if (margin + scaledWidth > Size) margin = Size - scaledWidth;
        if (margin < 0) margin = 0;

        return new Matrix(
            scale, 0,
            0, scale,
            margin - left * scale,
            margin - top * scale);
    }

    /// <summary>把 alpha 降采样成 grid×grid，相当于一次模糊后的轮廓强度。</summary>
    private static float[,]? PerceivedMask(Bitmap bitmap, int grid)
    {
        var pixelSize = bitmap.PixelSize;
        if (pixelSize.Width <= 0 || pixelSize.Height <= 0) return null;

        var mask = new float[grid, grid];

        try
        {
            using var copy = new WriteableBitmap(
                pixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var frame = copy.Lock();

            bitmap.CopyPixels(frame);

            for (var gy = 0; gy < grid; gy++)
            {
                var y0 = gy * pixelSize.Height / grid;
                var y1 = Math.Max(y0 + 1, (gy + 1) * pixelSize.Height / grid);

                for (var gx = 0; gx < grid; gx++)
                {
                    var x0 = gx * pixelSize.Width / grid;
                    var x1 = Math.Max(x0 + 1, (gx + 1) * pixelSize.Width / grid);

                    long sum = 0;
                    var count = 0;

                    for (var y = y0; y < y1; y++)
                    {
                        var row = IntPtr.Add(frame.Address, y * frame.RowBytes);
                        for (var x = x0; x < x1; x++)
                        {
                            sum += Marshal.ReadByte(row, x * 4 + 3);
                            count++;
                        }
                    }

                    mask[gx, gy] = count == 0 ? 0 : sum / (255f * count);
                }
            }
        }
        catch (Exception exception)
        {
            LauncherLog.Warn($"图标轮廓测量失败：{exception.Message}");
            return null;
        }

        return mask;
    }

    /// <summary>把预渲染结果按给定变换画到新的画布上。</summary>
    private sealed class BitmapCanvas : Control
    {
        public IImage? Source { get; init; }

        public Matrix Transform { get; init; } = Matrix.Identity;

        public override void Render(DrawingContext context)
        {
            if (Source is null) return;

            using (context.PushTransform(Transform))
            {
                context.DrawImage(Source, new Rect(0, 0, Source.Size.Width, Source.Size.Height));
            }
        }
    }

    private static string BlockPath(string name) => Path.Combine(BlocksDirectory, name + ".png");

    private static string SpritePath(string name) => Path.Combine(SpritesDirectory, name + ".png");
}
