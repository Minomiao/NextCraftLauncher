using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NextCraftLauncher.Core.Logging;
using NextCraftLauncher.Icons;

namespace NextCraftLauncher.Controls;

/// <summary>
/// 用项目内置贴图实时渲染一个等距 3D 方块。
/// <para>
/// 贴图在 <c>Assets/Icons/Blocks/&lt;方块&gt;/</c>，按原版的结构与配色绘制；
/// 模型由若干长方体组成，按相机剔除 + 深度排序，任意 yaw/pitch 都能画。
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

    /// <summary>要渲染的方块（对应 Assets/Icons/Blocks/&lt;名称&gt;/ 下的内置贴图）。</summary>
    public static readonly StyledProperty<string> BlockProperty =
        AvaloniaProperty.Register<BlockIcon, string>(nameof(Block), "grass");

    /// <summary>
    /// 按视觉轮廓对齐：让图标的轮廓贴着格子的左/上/下边，
    /// 配合行内统一的内边距就能得到三边等距。
    /// </summary>
    public static readonly StyledProperty<bool> AlignToOutlineProperty =
        AvaloniaProperty.Register<BlockIcon, bool>(nameof(AlignToOutline));

    private const string AssetRoot = "avares://NextCraftLauncher/Assets/Icons/Blocks/";

    static BlockIcon()
    {
        AffectsRender<BlockIcon>(BlockProperty, AlignToOutlineProperty, YawProperty, PitchProperty);
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

    public bool AlignToOutline
    {
        get => GetValue(AlignToOutlineProperty);
        set => SetValue(AlignToOutlineProperty, value);
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

        // 固定角度下优先用启动时预渲染好的图片，避免每次重建几何。
        if (Math.Abs(Yaw - IconCache.Yaw) < 0.001 &&
            Math.Abs(Pitch - IconCache.Pitch) < 0.001 &&
            IconCache.TryGet(Block) is { } cached)
        {
            var left = (Bounds.Width - size) / 2;
            var top = (Bounds.Height - size) / 2;

            using (context.PushRenderOptions(new RenderOptions
                   {
                       BitmapInterpolationMode = BitmapInterpolationMode.HighQuality,
                   }))
            {
                if (AlignToOutline && IconCache.TryGetOutline(Block) is { Height: > 0 } outline)
                {
                    var iconScale = size / outline.Height;
                    if (outline.Width * iconScale > size)
                    {
                        iconScale = size / outline.Width;
                    }

                    var matrix = new Matrix(
                        iconScale, 0,
                        0, iconScale,
                        left - outline.X * iconScale,
                        top - outline.Y * iconScale + (size - outline.Height * iconScale) / 2);

                    using (context.PushTransform(matrix))
                    {
                        context.DrawImage(cached, new Rect(0, 0, cached.Size.Width, cached.Size.Height));
                    }
                }
                else
                {
                    context.DrawImage(cached, new Rect(left, top, size, size));
                }
            }

            return;
        }

        var camera = CameraDirection(Yaw, Pitch);

        // 0. 有同名 Sprite 贴图时，把它渲染成有厚度的立体图形。
        var sprite = SpriteTextures.For(Block);
        var faces = sprite is not null
            ? BuildSpriteFaces(sprite, Yaw, Pitch)
            : (BlockTextures.For(Block) is { } textures
                ? BuildFaces(textures, ModelFor(Block))
                : null);

        if (faces is null) return;

        // 1. 收集可见面（法线朝向相机）并投影，投影结果跟着面一起排序。
        var visible = new List<(Face Face, Point[] Points)>(24);
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;

        foreach (var face in faces)
        {
            if (Dot(face.Normal, camera) <= 0.0001) continue;

            var center = default(Point3);
            foreach (var corner in face.Corners) center = center + corner;
            center = center * 0.25;

            var points = new Point[4];
            for (var c = 0; c < 4; c++)
            {
                var (sx, sy) = Project(face.Corners[c], Yaw, Pitch);
                points[c] = new Point(sx, sy);
                minX = Math.Min(minX, sx);
                maxX = Math.Max(maxX, sx);
                minY = Math.Min(minY, sy);
                maxY = Math.Max(maxY, sy);
            }

            visible.Add((face with { Depth = Dot(center, camera) }, points));
        }

        if (visible.Count == 0) return;

        // 2. 等比缩放居中。
        var spanX = Math.Max(0.0001, maxX - minX);
        var spanY = Math.Max(0.0001, maxY - minY);
        var scale = size * FillRatio / Math.Max(spanX, spanY);
        var offsetX = (Bounds.Width - spanX * scale) / 2 - minX * scale;
        var offsetY = (Bounds.Height - spanY * scale) / 2 - minY * scale;

        // 3. 从远到近绘制（画家算法，保证同样的角度得到同样的像素）。
        visible.Sort(static (a, b) => a.Face.Depth.CompareTo(b.Face.Depth));

        for (var i = 0; i < visible.Count; i++)
        {
            var points = visible[i].Points;
            var p0 = new Point(points[0].X * scale + offsetX, points[0].Y * scale + offsetY);
            var p1 = new Point(points[1].X * scale + offsetX, points[1].Y * scale + offsetY);
            var p3 = new Point(points[3].X * scale + offsetX, points[3].Y * scale + offsetY);

            var matrix = new Matrix(
                p1.X - p0.X, p1.Y - p0.Y,
                p3.X - p0.X, p3.Y - p0.Y,
                p0.X, p0.Y);

            matrix = visible[i].Face.Orientation * matrix;

            using (context.PushTransform(matrix))
            {
                var texture = visible[i].Face.Texture;
                var uv = visible[i].Face.Uv;
                var source = new Rect(
                    uv.X * texture.Size.Width,
                    uv.Y * texture.Size.Height,
                    uv.Width * texture.Size.Width,
                    uv.Height * texture.Size.Height);

                context.DrawImage(texture, source, new Rect(0, 0, 1, 1));

                var shade = visible[i].Face.Shade;
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

        public static Point3 operator -(Point3 a, Point3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public static Point3 operator *(Point3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    }

    private sealed record Face(
        Point3 Normal,
        Point3[] Corners,
        IImage Texture,
        Rect Uv,
        Matrix Orientation,
        double Shade,
        double Depth = 0);

    private sealed record ModelBox(Point3 Min, Point3 Max, (FaceKind Kind, FaceUv Uv)[] Faces);

    /// <summary>一个面用到的贴图区域，以及贴图在面内的旋转/镜像。</summary>
    private readonly record struct FaceUv(TextureSlot Slot, Rect Rect, Matrix Orientation);

    private enum TextureSlot
    {
        Top,
        Side,
        Bottom,
        Front,
    }

    /// <summary>原版单位坐标（0~16）转成以方块中心为原点的坐标。</summary>
    private static Point3 P(double x, double y, double z) => new((x - 8) / 16, (y - 8) / 16, (z - 8) / 16);

    /// <summary>贴图区域（0~16 像素）与旋转，负方向表示镜像。</summary>
    private static FaceUv Uv(
        double u0,
        double v0,
        double u1,
        double v1,
        TextureSlot slot = TextureSlot.Side,
        int rotation = 0)
    {
        var rect = new Rect(
            Math.Min(u0, u1) / 16,
            Math.Min(v0, v1) / 16,
            Math.Abs(u1 - u0) / 16,
            Math.Abs(v1 - v0) / 16);

        var orientation = Matrix.Identity;

        if (u1 < u0 || v1 < v0)
        {
            var sx = u1 < u0 ? -1d : 1d;
            var sy = v1 < v0 ? -1d : 1d;
            orientation = new Matrix(sx, 0, 0, sy, (1 - sx) / 2, (1 - sy) / 2);
        }

        if (rotation != 0)
        {
            var radians = rotation * Math.PI / 180;
            var cos = Math.Cos(radians);
            var sin = Math.Sin(radians);

            // 绕单位正方形中心旋转，让贴图在面内转动。
            var rotate = new Matrix(
                cos, sin, -sin, cos,
                0.5 - 0.5 * cos + 0.5 * sin,
                0.5 - 0.5 * sin - 0.5 * cos);

            orientation *= rotate;
        }

        return new FaceUv(slot, rect, orientation);
    }

    private static readonly ModelBox[] CubeModel =
    {
        new(P(0, 0, 0), P(16, 16, 16), new[]
        {
            (FaceKind.Up, Uv(0, 0, 16, 16, TextureSlot.Top)),
            (FaceKind.Down, Uv(0, 0, 16, 16, TextureSlot.Bottom)),
            (FaceKind.North, Uv(0, 0, 16, 16)),
            (FaceKind.South, Uv(0, 0, 16, 16, TextureSlot.Front)),
            (FaceKind.East, Uv(0, 0, 16, 16)),
            (FaceKind.West, Uv(0, 0, 16, 16)),
        }),
    };

    /// <summary>原版 template_anvil 的四个长方体与各自的贴图区域。</summary>
    private static readonly ModelBox[] AnvilModel =
    {
        new(P(2, 0, 2), P(14, 4, 14), new[]
        {
            (FaceKind.Down, Uv(2, 2, 14, 14, TextureSlot.Side, 180)),
            (FaceKind.Up, Uv(2, 2, 14, 14, TextureSlot.Side, 180)),
            (FaceKind.North, Uv(2, 12, 14, 16)),
            (FaceKind.South, Uv(2, 12, 14, 16)),
            (FaceKind.West, Uv(0, 2, 4, 14, TextureSlot.Side, 90)),
            (FaceKind.East, Uv(4, 2, 0, 14, TextureSlot.Side, 270)),
        }),
        new(P(4, 4, 3), P(12, 5, 13), new[]
        {
            (FaceKind.Up, Uv(4, 3, 12, 13, TextureSlot.Side, 180)),
            (FaceKind.North, Uv(4, 11, 12, 12)),
            (FaceKind.South, Uv(4, 11, 12, 12)),
            (FaceKind.West, Uv(4, 3, 5, 13, TextureSlot.Side, 90)),
            (FaceKind.East, Uv(5, 3, 4, 13, TextureSlot.Side, 270)),
        }),
        new(P(6, 5, 4), P(10, 10, 12), new[]
        {
            (FaceKind.North, Uv(6, 6, 10, 11)),
            (FaceKind.South, Uv(6, 6, 10, 11)),
            (FaceKind.West, Uv(5, 4, 10, 12, TextureSlot.Side, 90)),
            (FaceKind.East, Uv(10, 4, 5, 12, TextureSlot.Side, 270)),
        }),
        new(P(3, 10, 0), P(13, 16, 16), new[]
        {
            (FaceKind.Down, Uv(3, 0, 13, 16, TextureSlot.Side, 180)),
            (FaceKind.Up, Uv(3, 0, 13, 16, TextureSlot.Top, 180)),
            (FaceKind.North, Uv(3, 0, 13, 6)),
            (FaceKind.South, Uv(3, 0, 13, 6)),
            (FaceKind.West, Uv(10, 0, 16, 16, TextureSlot.Side, 90)),
            (FaceKind.East, Uv(16, 0, 10, 16, TextureSlot.Side, 270)),
        }),
    };

    private static IReadOnlyList<ModelBox> ModelFor(string block) =>
        string.Equals(block, "anvil", StringComparison.OrdinalIgnoreCase) ? AnvilModel : CubeModel;

    private static double ShadeFor(FaceKind kind) => kind switch
    {
        FaceKind.Up => 0d,
        FaceKind.Down => 0.45d,
        FaceKind.North => 0.30d,
        FaceKind.South => 0.18d,
        FaceKind.East => 0.40d,
        _ => 0.26d,
    };

    private static Point3 NormalFor(FaceKind kind) => kind switch
    {
        FaceKind.Up => new Point3(0, 1, 0),
        FaceKind.Down => new Point3(0, -1, 0),
        FaceKind.North => new Point3(0, 0, -1),
        FaceKind.South => new Point3(0, 0, 1),
        FaceKind.East => new Point3(1, 0, 0),
        _ => new Point3(-1, 0, 0),
    };

    /// <summary>四个角按贴图的左上、右上、右下、左下排列。</summary>
    private static Point3[] Corners(FaceKind kind, Point3 min, Point3 max) => kind switch
    {
        FaceKind.Up => new[]
        {
            new Point3(min.X, max.Y, min.Z), new Point3(max.X, max.Y, min.Z),
            new Point3(max.X, max.Y, max.Z), new Point3(min.X, max.Y, max.Z),
        },
        FaceKind.Down => new[]
        {
            new Point3(min.X, min.Y, max.Z), new Point3(max.X, min.Y, max.Z),
            new Point3(max.X, min.Y, min.Z), new Point3(min.X, min.Y, min.Z),
        },
        FaceKind.North => new[]
        {
            new Point3(min.X, max.Y, min.Z), new Point3(max.X, max.Y, min.Z),
            new Point3(max.X, min.Y, min.Z), new Point3(min.X, min.Y, min.Z),
        },
        FaceKind.South => new[]
        {
            new Point3(max.X, max.Y, max.Z), new Point3(min.X, max.Y, max.Z),
            new Point3(min.X, min.Y, max.Z), new Point3(max.X, min.Y, max.Z),
        },
        FaceKind.East => new[]
        {
            new Point3(max.X, max.Y, max.Z), new Point3(max.X, max.Y, min.Z),
            new Point3(max.X, min.Y, min.Z), new Point3(max.X, min.Y, max.Z),
        },
        _ => new[]
        {
            new Point3(min.X, max.Y, min.Z), new Point3(min.X, max.Y, max.Z),
            new Point3(min.X, min.Y, max.Z), new Point3(min.X, min.Y, min.Z),
        },
    };

    private static IEnumerable<Face> BuildFaces(BlockTextures textures, IReadOnlyList<ModelBox> model)
    {
        foreach (var box in model)
        {
            foreach (var (kind, uv) in box.Faces)
            {
                var texture = uv.Slot switch
                {
                    TextureSlot.Top => textures.Top,
                    TextureSlot.Bottom => textures.Bottom,
                    TextureSlot.Front => textures.Front,
                    _ => textures.Side,
                };

                yield return new Face(
                    NormalFor(kind),
                    Corners(kind, box.Min, box.Max),
                    texture,
                    uv.Rect,
                    uv.Orientation,
                    ShadeFor(kind));
            }
        }
    }

    // ---------------------------------------------------------------- 立体贴图

    /// <summary>把一张像素图按不透明像素拉出厚度，当作一块有厚度的牌子来画。</summary>
    private static IEnumerable<Face> BuildSpriteFaces(
        SpriteTextures sprite, double yawDegrees, double pitchDegrees)
    {
        var width = sprite.Width;
        var height = sprite.Height;
        if (width <= 0 || height <= 0) yield break;

        var pixel = 1d / Math.Max(width, height);
        var half = Math.Max(2, Math.Round(Math.Min(width, height) * 0.22)) * pixel / 2;

        var yaw = yawDegrees * Math.PI / 180;
        var depth = CameraDirection(yawDegrees, pitchDegrees);
        var right = new Point3(Math.Cos(yaw), 0, -Math.Sin(yaw));

        // 屏幕里的“向上”：垂直于相机与 right，并缩放到投影长度为 1。
        var up = Cross(depth, right);
        var projectedUp = Project(up, yawDegrees, pitchDegrees);
        var upLength = Math.Sqrt(projectedUp.X * projectedUp.X + projectedUp.Y * projectedUp.Y);
        if (upLength < 1e-6) yield break;
        up = up * (1 / upLength);
        if (Project(up, yawDegrees, pitchDegrees).Y > 0) up = up * -1;

        // 绕屏幕竖直轴把牌子转一点，这样既不变形，又能看到厚度。
        const double tilt = 22 * Math.PI / 180;
        var plateRight = right * Math.Cos(tilt) + depth * Math.Sin(tilt);
        var plateNormal = depth * Math.Cos(tilt) - right * Math.Sin(tilt);

        Point3 At(double u, double v, double d) => new(
            plateRight.X * u + up.X * v + plateNormal.X * d,
            plateRight.Y * u + up.Y * v + plateNormal.Y * d,
            plateRight.Z * u + up.Z * v + plateNormal.Z * d);

        var left = -width * pixel / 2;
        var top = height * pixel / 2;

        bool Opaque(int x, int y) =>
            x >= 0 && y >= 0 && x < width && y < height && sprite.Opaque[x, y];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!sprite.Opaque[x, y]) continue;

                var u0 = left + x * pixel;
                var u1 = u0 + pixel;
                var v1 = top - y * pixel;
                var v0 = v1 - pixel;
                var uv = new Rect((double)x / width, (double)y / height, 1d / width, 1d / height);

                yield return SpriteFace(
                    new[] { At(u0, v1, half), At(u1, v1, half), At(u1, v0, half), At(u0, v0, half) },
                    sprite.Image, uv, 0d);

                yield return SpriteFace(
                    new[] { At(u1, v1, -half), At(u0, v1, -half), At(u0, v0, -half), At(u1, v0, -half) },
                    sprite.Image, uv, 0.30d);

                if (!Opaque(x + 1, y))
                {
                    yield return SpriteFace(
                        new[] { At(u1, v1, half), At(u1, v1, -half), At(u1, v0, -half), At(u1, v0, half) },
                        sprite.Image, uv, 0.35d);
                }

                if (!Opaque(x - 1, y))
                {
                    yield return SpriteFace(
                        new[] { At(u0, v1, -half), At(u0, v1, half), At(u0, v0, half), At(u0, v0, -half) },
                        sprite.Image, uv, 0.26d);
                }

                if (!Opaque(x, y - 1))
                {
                    yield return SpriteFace(
                        new[] { At(u0, v1, -half), At(u1, v1, -half), At(u1, v1, half), At(u0, v1, half) },
                        sprite.Image, uv, 0d);
                }

                if (!Opaque(x, y + 1))
                {
                    yield return SpriteFace(
                        new[] { At(u0, v0, half), At(u1, v0, half), At(u1, v0, -half), At(u0, v0, -half) },
                        sprite.Image, uv, 0.45d);
                }
            }
        }
    }

    private static Face SpriteFace(Point3[] corners, IImage texture, Rect uv, double shade)
    {
        var normal = Normalize(Cross(corners[3] - corners[0], corners[1] - corners[0]));
        return new Face(normal, corners, texture, uv, Matrix.Identity, shade);
    }

    private static Point3 Cross(Point3 a, Point3 b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    private static Point3 Normalize(Point3 value)
    {
        var length = Math.Sqrt(value.X * value.X + value.Y * value.Y + value.Z * value.Z);
        return length < 1e-6
            ? new Point3(0, 0, 0)
            : new Point3(value.X / length, value.Y / length, value.Z / length);
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

    private sealed record BlockTextures(IImage Top, IImage Side, IImage Bottom, IImage Front)
    {
        private static readonly Dictionary<string, BlockTextures?> Cache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>项目内置贴图，随程序集编译，不依赖 Minecraft 客户端。</summary>
        public static BlockTextures? For(string block)
        {
            if (string.IsNullOrWhiteSpace(block)) return null;

            lock (Cache)
            {
                if (Cache.TryGetValue(block, out var cached)) return cached;
            }

            var loaded = Load(block);

            lock (Cache)
            {
                Cache[block] = loaded;
            }

            return loaded;
        }

        private static BlockTextures? Load(string block)
        {
            try
            {
                var top = Read(block, "top");
                var side = Read(block, "side") ?? top;
                var bottom = Read(block, "bottom") ?? side;
                var front = Read(block, "front") ?? side;

                if (top is null || side is null || bottom is null || front is null)
                {
                    LauncherLog.Warn($"方块图标贴图缺失：Assets/Icons/Blocks/{block}");
                    return null;
                }

                return new BlockTextures(top, side, bottom, front);
            }
            catch (Exception exception)
            {
                LauncherLog.Warn($"加载方块图标贴图失败（{block}）：{exception.Message}");
                return null;
            }
        }

        private static Bitmap? Read(string block, string face)
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

    /// <summary>有透明背景的图标：按不透明像素拉出厚度。</summary>
    private sealed record SpriteTextures(IImage Image, bool[,] Opaque, int Width, int Height)
    {
        private const string SpriteRoot = "avares://NextCraftLauncher/Assets/Icons/Sprites/";

        private static readonly Dictionary<string, SpriteTextures?> Cache =
            new(StringComparer.OrdinalIgnoreCase);

        public static SpriteTextures? For(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            lock (Cache)
            {
                if (Cache.TryGetValue(name, out var cached)) return cached;
            }

            var loaded = Load(name);

            lock (Cache)
            {
                Cache[name] = loaded;
            }

            return loaded;
        }

        private static SpriteTextures? Load(string name)
        {
            try
            {
                using var stream = AssetLoader.Open(new Uri($"{SpriteRoot}{name}.png"));
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                memory.Position = 0;

                var bitmap = new Bitmap(memory);
                var width = bitmap.PixelSize.Width;
                var height = bitmap.PixelSize.Height;
                if (width <= 0 || height <= 0) return null;

                var opaque = new bool[width, height];
                using (var copy = new WriteableBitmap(
                           bitmap.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul))
                using (var frame = copy.Lock())
                {
                    bitmap.CopyPixels(frame);

                    for (var y = 0; y < height; y++)
                    {
                        var row = IntPtr.Add(frame.Address, y * frame.RowBytes);
                        for (var x = 0; x < width; x++)
                        {
                            opaque[x, y] = Marshal.ReadByte(row, x * 4 + 3) >= 128;
                        }
                    }
                }

                return new SpriteTextures(bitmap, opaque, width, height);
            }
            catch (Exception)
            {
                // 没有同名 Sprite 时退回方块渲染，不当作错误。
                return null;
            }
        }
    }
}
