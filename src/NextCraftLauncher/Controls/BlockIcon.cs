using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NextCraftLauncher.Core.Logging;

namespace NextCraftLauncher.Controls;

/// <summary>
/// 用项目内自带的方块贴图，实时渲染一个等距 3D 方块。
/// <para>
/// 贴图放在 <c>Assets/Icons/Blocks/</c>（随程序集编译，不依赖 Minecraft 客户端）；
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

    private const string AssetRoot = "avares://NextCraftLauncher/Assets/Icons/Blocks/";

    static BlockIcon()
    {
        AffectsRender<BlockIcon>(YawProperty, PitchProperty);
    }

    public BlockIcon()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
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

        var textures = GrassTextures.Asset;
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
        double Depth = 0);

    /// <summary>草方块：单位立方体，六面贴图。</summary>
    private static IEnumerable<Face> BuildCubeFaces(GrassTextures textures)
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
            0d);

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

    private sealed record GrassTextures(IImage Top, IImage Side, IImage Bottom)
    {
        private static GrassTextures? _instance;
        private static bool _loaded;

        /// <summary>项目内置贴图，随程序集编译，不依赖 Minecraft 客户端。</summary>
        public static GrassTextures? Asset
        {
            get
            {
                if (_loaded) return _instance;

                _loaded = true;

                try
                {
                    var top = Load("grass_top");
                    var side = Load("grass_side");
                    var bottom = Load("dirt");

                    if (top is null || side is null || bottom is null)
                    {
                        LauncherLog.Warn("方块图标贴图缺失（Assets/Icons/Blocks）");
                        return null;
                    }

                    _instance = new GrassTextures(top, side, bottom);
                }
                catch (Exception exception)
                {
                    LauncherLog.Warn($"加载方块图标贴图失败：{exception.Message}");
                }

                return _instance;
            }
        }

        private static Bitmap? Load(string name)
        {
            var uri = new Uri($"{AssetRoot}{name}.png");
            using var stream = AssetLoader.Open(uri);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            memory.Position = 0;
            return new Bitmap(memory);
        }
    }
}
