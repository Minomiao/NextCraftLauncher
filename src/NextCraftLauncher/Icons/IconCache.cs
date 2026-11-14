using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Media.Imaging;
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
    /// <summary>图标素材有改动时加一，旧缓存会整体重建。</summary>
    private const int CacheVersion = 1;

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

        using var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        bitmap.Render(icon);
        bitmap.Save(path);
    }

    private static string BlockPath(string name) => Path.Combine(BlocksDirectory, name + ".png");

    private static string SpritePath(string name) => Path.Combine(SpritesDirectory, name + ".png");
}
