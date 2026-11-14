using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NextCraftLauncher.Core.Logging;

namespace NextCraftLauncher.Controls;

/// <summary>
/// 实时渲染一个等距 3D 方块。
/// <para>
/// 贴图优先从当前版本客户端 jar 里读原版贴图（观感与原版一致）；
/// 没装游戏时退回 <c>Assets/Icons/Blocks/</c> 里的内置贴图。
/// 六面模型 + 按相机剔除 + 深度排序，任意 yaw/pitch 都能画。
/// </para>
/// </summary>
public sealed class BlockIcon : Control
{
    /// <summary>水平旋转角（度）。</summary>
    public static readonly StyledProperty<double> YawProperty =
        AvaloniaProperty.Register<BlockIcon, double>(nameof(Yaw), 45d);

    /// <summary>俯仰角（度）。</summary>
    public static readonly StyledProperty<double> PitchProperty =
        AvaloniaProperty.Register<BlockIcon, double>(nameof(Pitch), 30d);

    /// <summary>方块在画布中的占比（0~1），留出边距。</summary>
    private const double FillRatio = 0.92;

    /// <summary>原版草顶贴图是灰度的，用它上色。</summary>
    private static readonly IBrush GrassTintBrush =
        new SolidColorBrush(Color.FromArgb(0x8C, 0x91, 0xBD, 0x59));

    /// <summary>要渲染的方块（对应 Assets/Icons/Blocks/&lt;名称&gt;/ 下的内置贴图）。</summary>
    public static readonly StyledProperty<string> BlockProperty =
        AvaloniaProperty.Register<BlockIcon, string>(nameof(Block), "grass");

    /// <summary>可选：从这个客户端 jar 里读原版贴图。</summary>
    public static readonly StyledProperty<string?> ClientJarPathProperty =
        AvaloniaProperty.Register<BlockIcon, string?>(nameof(ClientJarPath));

    private const string AssetRoot = "avares://NextCraftLauncher/Assets/Icons/Blocks/";

    static BlockIcon()
    {
        AffectsRender<BlockIcon>(BlockProperty, ClientJarPathProperty, YawProperty, PitchProperty);
    }

    public BlockIcon()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    public string Block
    {
        get => GetValue(BlockProperty);
        set => SetValue(BlockProperty, value);
    }

    public string? ClientJarPath
    {
        get => GetValue(ClientJarPathProperty);
        set => SetValue(ClientJarPathProperty, value);
    }

    public double Yaw
    {
        get => GetValue(YawProperty);
        set => SetValue(YawProperty, value);
    }

    public double Pitch
    {
        get => GetValue(PitchProperty);
        set => SetValue(PitchProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;

        var textures = BlockTextures.For(Block, ClientJarPath);
        if (textures is null) return;

        var camera = CameraDirection(Yaw, Pitch);

        // 1. 收集可见面（法线朝向相机）。
        var visible = new List<Face>(3);
        foreach (var face in BuildCubeFaces(textures))
        {
            if (Dot(face.Normal, camera) <= 0.0001) continue;

            var center = default(Point3);
            foreach (var corner in face.Corners) center = center + corner;
            center = center * 0.25;

            visible.Add(face with { Depth = Dot(center, camera) });
        }

        if (visible.Count == 0) return;

        // 2. 投影：取所有可见角点，等比缩放居中。
        var projected = new Point[visible.Count][];
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;

        for (var i = 0; i < visible.Count; i++)
        {
            var corners = visible[i].Corners;
            var points = new Point[4];

            for (var c = 0; c < 4; c++)
            {
                var (sx, sy) = Project(corners[c], Yaw, Pitch);
                points[c] = new Point(sx, sy);
                minX = Math.Min(minX, sx);
                maxX = Math.Max(maxX, sx);
                minY = Math.Min(minY, sy);
                maxY = Math.Max(maxY, sy);
            }

            projected[i] = points;
        }

        var spanX = Math.Max(0.0001, maxX - minX);
        var spanY = Math.Max(0.0001, maxY - minY);
        var scale = size * FillRatio / Math.Max(spanX, spanY);
        var offsetX = (Bounds.Width - spanX * scale) / 2 - minX * scale;
        var offsetY = (Bounds.Height - spanY * scale) / 2 - minY * scale;

        // 3. 从远到近绘制（画家算法，保证同样的角度得到同样的像素）。
        visible.Sort(static (a, b) => a.Depth.CompareTo(b.Depth));

        for (var i = 0; i < visible.Count; i++)
        {
            var points = projected[i];
            var p0 = new Point(points[0].X * scale + offsetX, points[0].Y * scale + offsetY);
            var p1 = new Point(points[1].X * scale + offsetX, points[1].Y * scale + offsetY);
            var p3 = new Point(points[3].X * scale + offsetX, points[3].Y * scale + offsetY);

            var matrix = new Matrix(
                p1.X - p0.X, p1.Y - p0.Y,
                p3.X - p0.X, p3.Y - p0.Y,
                p0.X, p0.Y);

            using (context.PushTransform(matrix))
            {
                context.DrawImage(visible[i].Texture, new Rect(0, 0, 1, 1));

                // 原版草方块顶面是灰度的，靠生物群系染色；内置贴图已经是绿的，不需要叠色。
                if (visible[i].Tint)
                {
                    context.DrawRectangle(GrassTintBrush, null, new Rect(0, 0, 1, 1));
                }

                var shade = visible[i].Shade;
                if (shade > 0)
                {
                    context.DrawRectangle(
                        new SolidColorBrush(Colors.Black, shade), null, new Rect(0, 0, 1, 1));
                }
            }
        }
    }

    // ---------------------------------------------------------------- 模型

    private enum FaceKind
    {
        Up,
        Down,
        North,
        South,
        East,
        West,
    }

    private readonly record struct Point3(double X, double Y, double Z)
    {
        public static Point3 operator +(Point3 a, Point3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public static Point3 operator *(Point3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    }

    private sealed record Face(
        Point3 Normal,
        Point3[] Corners,
        IImage Texture,
        double Shade,
        bool Tint = false,
        double Depth = 0);

    /// <summary>单位立方体，六面贴图。</summary>
    private static IEnumerable<Face> BuildCubeFaces(BlockTextures textures)
    {
        const double h = 0.5;

        yield return new Face(
            new Point3(0, 1, 0),
            new[]
            {
                new Point3(-h, h, -h), new Point3(h, h, -h),
                new Point3(h, h, h), new Point3(-h, h, h),
            },
            textures.Top,
            0d,
            textures.TintTop);

        yield return new Face(
            new Point3(0, -1, 0),
            new[]
            {
                new Point3(-h, -h, h), new Point3(h, -h, h),
                new Point3(h, -h, -h), new Point3(-h, -h, -h),
            },
            textures.Bottom,
            0.45d);

        yield return new Face(
            new Point3(0, 0, -1),
            new[]
            {
                new Point3(-h, h, -h), new Point3(h, h, -h),
                new Point3(h, -h, -h), new Point3(-h, -h, -h),
            },
            textures.Side,
            0.30d);

        yield return new Face(
            new Point3(0, 0, 1),
            new[]
            {
                new Point3(h, h, h), new Point3(-h, h, h),
                new Point3(-h, -h, h), new Point3(h, -h, h),
            },
            textures.Side,
            0.18d);

        yield return new Face(
            new Point3(1, 0, 0),
            new[]
            {
                new Point3(h, h, h), new Point3(h, h, -h),
                new Point3(h, -h, -h), new Point3(h, -h, h),
            },
            textures.Side,
            0.40d);

        yield return new Face(
            new Point3(-1, 0, 0),
            new[]
            {
                new Point3(-h, h, -h), new Point3(-h, h, h),
                new Point3(-h, -h, h), new Point3(-h, -h, -h),
            },
            textures.Side,
            0.26d);
    }

    // ---------------------------------------------------------------- 投影

    /// <summary>正交等距投影：先绕 Y 轴旋转，再按俯仰角压扁。</summary>
    private static (double X, double Y) Project(Point3 point, double yawDegrees, double pitchDegrees)
    {
        var yaw = yawDegrees * Math.PI / 180;
        var pitch = pitchDegrees * Math.PI / 180;

        var cosYaw = Math.Cos(yaw);
        var sinYaw = Math.Sin(yaw);

        var rotatedX = point.X * cosYaw - point.Z * sinYaw;
        var rotatedZ = point.X * sinYaw + point.Z * cosYaw;

        return (rotatedX, rotatedZ * Math.Sin(pitch) - point.Y * Math.Cos(pitch));
    }

    /// <summary>
    /// 相机朝向（从场景指向相机）。它是投影的两个零空间方向，
    /// 用它可以做面剔除和深度排序。
    /// </summary>
    private static Point3 CameraDirection(double yawDegrees, double pitchDegrees)
    {
        var yaw = yawDegrees * Math.PI / 180;
        var pitch = pitchDegrees * Math.PI / 180;

        var direction = new Point3(
            Math.Tan(yaw),
            Math.Tan(pitch) / Math.Cos(yaw),
            1);

        var length = Math.Sqrt(
            direction.X * direction.X + direction.Y * direction.Y + direction.Z * direction.Z);

        return new Point3(direction.X / length, direction.Y / length, direction.Z / length);
    }

    private static double Dot(Point3 a, Point3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    // ---------------------------------------------------------------- 贴图

    private sealed record BlockTextures(IImage Top, IImage Side, IImage Bottom, bool TintTop = false)
    {
        private static readonly Dictionary<string, BlockTextures?> Cache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>原版贴图优先，取不到时退回项目内置贴图。</summary>
        public static BlockTextures? For(string block, string? jarPath)
        {
            if (string.IsNullOrWhiteSpace(block)) return null;

            var key = string.IsNullOrWhiteSpace(jarPath) ? block : block + "|" + jarPath;

            lock (Cache)
            {
                if (Cache.TryGetValue(key, out var cached)) return cached;
            }

            var loaded = ReadFromJar(block, jarPath) ?? ReadBundled(block);

            lock (Cache)
            {
                Cache[key] = loaded;
            }

            return loaded;
        }

        // ------------------------------------------------ 原版贴图（客户端 jar）

        private static BlockTextures? ReadFromJar(string block, string? jarPath)
        {
            if (string.IsNullOrWhiteSpace(jarPath) || !File.Exists(jarPath)) return null;
            if (JarNames(block) is not { } names) return null;

            try
            {
                using var archive = ZipFile.OpenRead(jarPath);

                var top = ReadEntry(archive, names.Top);
                var side = ReadEntry(archive, names.Side) ?? top;
                var bottom = ReadEntry(archive, names.Bottom) ?? side;

                if (top is null || side is null || bottom is null) return null;

                // 原版草方块顶面是灰度贴图，需要按生物群系上色。
                return new BlockTextures(top, side, bottom, TintTop: block == "grass");
            }
            catch (Exception exception)
            {
                LauncherLog.Warn($"读取原版方块贴图失败（{block}）：{exception.Message}");
                return null;
            }
        }

        private static Bitmap? ReadEntry(ZipArchive archive, string[] names)
        {
            foreach (var name in names)
            {
                var entry = archive.GetEntry($"assets/minecraft/textures/block/{name}.png")
                            ?? archive.GetEntry($"assets/minecraft/textures/blocks/{name}.png");

                if (entry is null) continue;

                using var stream = entry.Open();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                memory.Position = 0;

                return new Bitmap(memory);
            }

            return null;
        }

        /// <summary>方块 → jar 内贴图名（1.13+ 名在前，旧版名在后）。</summary>
        private static (string[] Top, string[] Side, string[] Bottom)? JarNames(string block) => block switch
        {
            "grass" => (
                new[] { "grass_block_top", "grass_top" },
                new[] { "grass_block_side", "grass_side" },
                new[] { "dirt" }),
            "dirt" => Same("dirt"),
            "cobblestone" => Same("cobblestone"),
            "gold" => Same("gold_block"),
            "redstone" => Same("redstone_block"),
            "lamp_on" => Same("redstone_lamp_on"),
            "lamp_off" => Same("redstone_lamp", "redstone_lamp_off"),
            "path" => (
                new[] { "dirt_path_top", "grass_path_top" },
                new[] { "dirt_path_side", "grass_path_side" },
                new[] { "dirt" }),
            "command_block" => Same("command_block_side", "command_block"),
            _ => null,
        };

        private static (string[] Top, string[] Side, string[] Bottom) Same(params string[] names)
            => (names, names, names);

        // ------------------------------------------------ 内置贴图（项目资源）

        private static BlockTextures? ReadBundled(string block)
        {
            try
            {
                var top = ReadAsset(block, "top");
                var side = ReadAsset(block, "side") ?? top;
                var bottom = ReadAsset(block, "bottom") ?? side;

                if (top is null || side is null || bottom is null)
                {
                    LauncherLog.Warn($"方块图标贴图缺失：Assets/Icons/Blocks/{block}");
                    return null;
                }

                return new BlockTextures(top, side, bottom);
            }
            catch (Exception exception)
            {
                LauncherLog.Warn($"加载内置方块贴图失败（{block}）：{exception.Message}");
                return null;
            }
        }

        private static Bitmap? ReadAsset(string block, string face)
        {
            var uri = new Uri($"{AssetRoot}{block}/{face}.png");

            try
            {
                using var stream = AssetLoader.Open(uri);
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                memory.Position = 0;
                return new Bitmap(memory);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
