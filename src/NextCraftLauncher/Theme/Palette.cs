using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using NextCraftLauncher.Core.Logging;
using NextCraftLauncher.Core.Settings;

namespace NextCraftLauncher.Theme;

/// <summary>
/// 运行时生成的颜色配置。
///
/// 基础色（<see cref="PaletteSeed"/>）在这里算成各种变种，注册成应用资源，
/// XAML 与控件代码只按资源名取用，不出现颜色字面量。
/// 需要改色时调用 <see cref="Apply"/>：资源对象不换，只改画刷的颜色，
/// 因此界面会即时刷新，不用重建窗口或重新解析 XAML。
///
/// 算好的颜色会缓存到 <c>NCL/cache/palette.json</c>，种子与强调色没变时
/// 下次启动直接读缓存，不重复计算。
///
/// 错误与警告是固定色，不参与派生。
/// </summary>
public static class Palette
{
    // ===== 固定色：永远不变，两个主题用同一组值 =====

    private static readonly Color ErrorFill = Color.Parse("#D13438");
    private static readonly Color ErrorFillHover = Color.Parse("#C42B1C");
    private static readonly Color ErrorFillPressed = Color.Parse("#A4262C");
    private static readonly Color WarningFill = Color.Parse("#C77B00");
    private static readonly Color OnFillText = Colors.White;

    /// <summary>拿不到系统强调色时的默认值。</summary>
    private static readonly Color FallbackAccent = Color.Parse("#0078D4");

    /// <summary>所有资源键；每个键对应一个长期存在的画刷实例。</summary>
    private static readonly string[] Keys =
    {
        "ErrorFillBrush",
        "ErrorFillHoverBrush",
        "ErrorFillPressedBrush",
        "ErrorTextBrush",
        "WarningFillBrush",
        "WarningTextBrush",
        "OnFillTextBrush",
        "OverlayMaskBrush",
        "ImagePlaceholderBrush",
        "TaskCardBackgroundBrush",
        "TaskCardBorderBrush",
        "RowActionBackgroundBrush",
        "RowActionBackgroundHoverBrush",
        "RowActionBackgroundPressedBrush",
        "AccentFillBrush",
        "AccentTextBrush",
        "AccentBorderBrush",
        "PageBackgroundBrush",
        "SoftButtonBrush",
        "SoftButtonHoverBrush",
        "PanelFillBrush",
        "PanelFillHoverBrush",
    };

    private static readonly Dictionary<string, SolidColorBrush> LightBrushes = CreateBrushes();
    private static readonly Dictionary<string, SolidColorBrush> DarkBrushes = CreateBrushes();

    /// <summary>缓存格式版本；变种算法变了就改这个值，旧缓存自动失效。</summary>
    private const int CacheVersion = 8;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static Application? _application;

    private static string CacheFilePath =>
        Path.Combine(AppSettings.DataDirectory, "cache", "palette.json");

    /// <summary>当前生效的颜色种子。</summary>
    public static PaletteSeed Seed { get; private set; } = new();

    /// <summary>注册到应用资源；必须在创建窗口之前调用一次。</summary>
    public static void Register(Application application)
    {
        RegisterVariant(application, ThemeVariant.Light, LightBrushes);
        RegisterVariant(application, ThemeVariant.Dark, DarkBrushes);

        // 没有明确主题时按浅色处理；每个主题键必须是独立的字典实例。
        RegisterVariant(application, ThemeVariant.Default, LightBrushes);

        _application = application;

        var accent = ResolveAccent();

        if (TryLoadCache(Seed, accent, out var light, out var dark))
        {
            SetColors(light, dark);
        }
        else
        {
            light = Compute(Seed, dark: false, accent);
            dark = Compute(Seed, dark: true, accent);

            SetColors(light, dark);
            SaveCache(Seed, accent, light, dark);
        }

        // 种子没指定强调色时跟随系统：系统换色后重新算一遍。
        if (application.PlatformSettings is { } platformSettings)
        {
            platformSettings.ColorValuesChanged += (_, _) =>
                Dispatcher.UIThread.Post(() => Apply(Seed));
        }
    }

    /// <summary>
    /// 换一套颜色：重新计算变种并写回画刷。可在运行时随时调用（需在 UI 线程）。
    /// </summary>
    public static void Apply(PaletteSeed seed)
    {
        ArgumentNullException.ThrowIfNull(seed);

        Seed = seed;

        var accent = ResolveAccent();
        var light = Compute(seed, dark: false, accent);
        var dark = Compute(seed, dark: true, accent);

        SetColors(light, dark);
        SaveCache(seed, accent, light, dark);
    }

    /// <summary>
    /// 只换强调色，其余种子保持不变。传 <c>null</c> 表示重新跟随系统。
    /// </summary>
    public static void ApplyAccent(Color? accent) => Apply(Seed with { Accent = accent });

    private static void SetColors(
        IReadOnlyDictionary<string, Color> light,
        IReadOnlyDictionary<string, Color> dark)
    {
        foreach (var (key, color) in light) LightBrushes[key].Color = color;
        foreach (var (key, color) in dark) DarkBrushes[key].Color = color;
    }

    private static void RegisterVariant(
        Application application,
        ThemeVariant variant,
        IReadOnlyDictionary<string, SolidColorBrush> brushes)
    {
        var dictionary = new ResourceDictionary();
        foreach (var key in Keys) dictionary[key] = brushes[key];

        application.Resources.ThemeDictionaries[variant] = dictionary;
    }

    /// <summary>缓存签名：种子、强调色与缓存版本一样时才能复用。</summary>
    private static string Signature(PaletteSeed seed, Color accent) => string.Concat(
        CacheVersion.ToString(),
        "|", seed.Neutral.ToUInt32().ToString("X8"),
        "|", seed.CardSurface.ToUInt32().ToString("X8"),
        "|", seed.CardBorder.ToUInt32().ToString("X8"),
        "|", accent.ToUInt32().ToString("X8"));

    private static bool TryLoadCache(
        PaletteSeed seed,
        Color accent,
        out Dictionary<string, Color> light,
        out Dictionary<string, Color> dark)
    {
        light = new Dictionary<string, Color>(StringComparer.Ordinal);
        dark = new Dictionary<string, Color>(StringComparer.Ordinal);

        try
        {
            if (!File.Exists(CacheFilePath)) return false;

            var cache = JsonSerializer.Deserialize<PaletteCache>(File.ReadAllText(CacheFilePath));
            if (cache is null || cache.Version != CacheVersion) return false;
            if (!string.Equals(cache.Signature, Signature(seed, accent), StringComparison.Ordinal)) return false;

            light = ParseColors(cache.Light);
            dark = ParseColors(cache.Dark);

            return light.Count == Keys.Length && dark.Count == Keys.Length;
        }
        catch (Exception exception)
        {
            // 缓存坏了就当没有，重新计算并覆盖。
            LauncherLog.Warn($"颜色缓存读取失败：{exception.Message}");
            return false;
        }
    }

    private static void SaveCache(
        PaletteSeed seed,
        Color accent,
        Dictionary<string, Color> light,
        Dictionary<string, Color> dark)
    {
        try
        {
            var directory = Path.GetDirectoryName(CacheFilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var cache = new PaletteCache
            {
                Version = CacheVersion,
                Signature = Signature(seed, accent),
                Light = ToText(light),
                Dark = ToText(dark),
            };

            File.WriteAllText(CacheFilePath, JsonSerializer.Serialize(cache, JsonOptions));
        }
        catch (Exception exception)
        {
            // 写缓存失败不影响显示，下次启动重新算。
            LauncherLog.Warn($"颜色缓存写入失败：{exception.Message}");
        }
    }

    private static Dictionary<string, Color> ParseColors(Dictionary<string, string> source)
    {
        var colors = new Dictionary<string, Color>(Keys.Length, StringComparer.Ordinal);

        foreach (var key in Keys)
        {
            if (!source.TryGetValue(key, out var text)) continue;
            if (Color.TryParse(text, out var color)) colors[key] = color;
        }

        return colors;
    }

    private static Dictionary<string, string> ToText(IReadOnlyDictionary<string, Color> colors)
    {
        var text = new Dictionary<string, string>(colors.Count, StringComparer.Ordinal);

        // 统一写成 #AARRGGBB，避免出现 "White" 这类颜色名。
        foreach (var (key, color) in colors)
            text[key] = "#" + color.ToUInt32().ToString("X8", CultureInfo.InvariantCulture);

        return text;
    }

    /// <summary>缓存文件的内容。</summary>
    private sealed class PaletteCache
    {
        public int Version { get; set; }

        public string Signature { get; set; } = string.Empty;

        public Dictionary<string, string> Light { get; set; } = new();

        public Dictionary<string, string> Dark { get; set; } = new();
    }

    /// <summary>系统强调色（不受种子影响）。</summary>
    public static Color SystemAccent
    {
        get
        {
            if (_application?.TryFindResource("SystemAccentColor", out var value) == true && value is Color fromTheme)
                return fromTheme;

            var platform = _application?.PlatformSettings?.GetColorValues().AccentColor1;
            if (platform is { A: > 0 } fromPlatform) return fromPlatform;

            return FallbackAccent;
        }
    }

    /// <summary>强调色：种子指定了就用指定的，否则跟随系统。</summary>
    private static Color ResolveAccent() => Seed.Accent ?? SystemAccent;

    private static Dictionary<string, Color> Compute(PaletteSeed seed, bool dark, Color accent)
    {
        var morandi = dark ? MorandiDark(accent) : Morandi(accent);

        return new Dictionary<string, Color>
        {
            // 固定色
            ["ErrorFillBrush"] = ErrorFill,
            ["ErrorFillHoverBrush"] = ErrorFillHover,
            ["ErrorFillPressedBrush"] = ErrorFillPressed,
            ["ErrorTextBrush"] = ErrorFill,
            ["WarningFillBrush"] = WarningFill,
            ["WarningTextBrush"] = WarningFill,
            ["OnFillTextBrush"] = OnFillText,

            // 强调色：三个角色同色，将来要分别调时只改这里
            ["AccentFillBrush"] = accent,
            ["AccentTextBrush"] = accent,
            ["AccentBorderBrush"] = accent,

            // 背景：亮色用近白淡色，深色用近黑暗色；跟随系统时保持主题默认表面
            ["PageBackgroundBrush"] = seed.Accent is null
                ? dark ? Colors.Transparent : Colors.White
                : dark ? PaleDark(accent) : Pale(accent),

            // 面板层次：浮层用带 alpha 的莫兰迪色，深色主题叠得更重
            ["SoftButtonBrush"] = morandi,
            ["SoftButtonHoverBrush"] = dark ? Lighten(morandi, 0.08) : Darken(morandi, 0.08),
            ["PanelFillBrush"] = Alpha(morandi, dark ? (byte)0x2E : (byte)0x1A),
            ["PanelFillHoverBrush"] = Alpha(morandi, dark ? (byte)0x3D : (byte)0x24),

            // 由种子派生
            ["OverlayMaskBrush"] = Alpha(Colors.Black, 0x66),
            ["ImagePlaceholderBrush"] = Alpha(Grayscale(seed.Neutral), 0x24),
            ["TaskCardBackgroundBrush"] = dark ? Alpha(Colors.White, 0x2A) : seed.CardSurface,
            ["TaskCardBorderBrush"] = dark ? Alpha(Colors.White, 0x33) : seed.CardBorder,
            ["RowActionBackgroundBrush"] = Alpha(morandi, dark ? (byte)0x2E : (byte)0x14),
            ["RowActionBackgroundHoverBrush"] = Alpha(morandi, dark ? (byte)0x40 : (byte)0x1F),
            ["RowActionBackgroundPressedBrush"] = Alpha(morandi, dark ? (byte)0x52 : (byte)0x29),
        };
    }

    private static Dictionary<string, SolidColorBrush> CreateBrushes()
    {
        var brushes = new Dictionary<string, SolidColorBrush>(Keys.Length, StringComparer.Ordinal);
        foreach (var key in Keys) brushes[key] = new SolidColorBrush(Colors.Transparent);
        return brushes;
    }

    /// <summary>从 <paramref name="from"/> 向 <paramref name="to"/> 混合；0 保持原色，1 完全变成目标色。</summary>
    public static Color Blend(Color from, Color to, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            from.A,
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));
    }

    /// <summary>亮色变种：向白色靠拢。</summary>
    public static Color Lighten(Color color, double amount) => Blend(color, Colors.White, amount);

    /// <summary>
    /// 近白淡色（亮色主题）：极浅、几乎就是白色，只保留一丝色相。
    /// H 不变，S 压到 1.2% 以内，V 抬到 99% 以上。
    /// </summary>
    public static Color Pale(Color color)
    {
        var (hue, saturation, value) = ToHsv(color);
        return FromHsv(hue, Math.Min(saturation * 0.012, 0.012), Math.Min(value * 0.01 + 0.99, 1), color.A);
    }

    /// <summary>
    /// 近黑暗色（深色主题的对应式）：保留同样的微弱色相，明度压到 4%~12%。
    /// H 不变，S 同样压到 18% 以内，V 取 0.04 + 0.08·V。
    /// </summary>
    public static Color PaleDark(Color color)
    {
        var (hue, saturation, value) = ToHsv(color);
        return FromHsv(hue, Math.Min(saturation * 0.18, 0.18), 0.04 + value * 0.08, color.A);
    }

    /// <summary>
    /// 莫兰迪色（亮色主题）：低饱和灰调。
    /// H 不变，S 降到 35%，V 压到 60%~85% 之间。
    /// </summary>
    public static Color Morandi(Color color)
    {
        var (hue, saturation, value) = ToHsv(color);
        return FromHsv(hue, saturation * 0.35, Math.Min(value * 0.25 + 0.6, 0.85), color.A);
    }

    /// <summary>
    /// 莫兰迪色（深色主题）：同样低饱和，明度压到 30%~55%。
    /// H 不变，S 降到 35%，V 取 0.30 + 0.25·V。
    /// </summary>
    public static Color MorandiDark(Color color)
    {
        var (hue, saturation, value) = ToHsv(color);
        return FromHsv(hue, saturation * 0.35, 0.30 + value * 0.25, color.A);
    }

    /// <summary>暗色变种：向黑色靠拢。</summary>
    public static Color Darken(Color color, double amount) => Blend(color, Colors.Black, amount);

    /// <summary>灰色变种：按感知亮度去饱和，只保留明暗。</summary>
    public static Color Grayscale(Color color)
    {
        var value = (byte)Math.Round(color.R * 0.299 + color.G * 0.587 + color.B * 0.114);
        return Color.FromArgb(color.A, value, value, value);
    }

    /// <summary>透明变种：保留 RGB，替换 alpha。</summary>
    public static Color Alpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>RGB → HSV；H 取 0~360，S / V 取 0~1。</summary>
    private static (double Hue, double Saturation, double Value) ToHsv(Color color)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        double hue;
        if (delta <= 0) hue = 0;
        else if (max == r) hue = 60 * (((g - b) / delta) % 6);
        else if (max == g) hue = 60 * ((b - r) / delta + 2);
        else hue = 60 * ((r - g) / delta + 4);

        if (hue < 0) hue += 360;

        var saturation = max <= 0 ? 0 : delta / max;
        return (hue, saturation, max);
    }

    /// <summary>HSV → RGB；alpha 由调用方决定。</summary>
    private static Color FromHsv(double hue, double saturation, double value, byte alpha)
    {
        var s = Math.Clamp(saturation, 0, 1);
        var v = Math.Clamp(value, 0, 1);
        var h = ((hue % 360) + 360) % 360;

        var chroma = v * s;
        var x = chroma * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - chroma;

        var (r, g, b) = h switch
        {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x),
        };

        return Color.FromArgb(
            alpha,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}

/// <summary>
/// 颜色种子：界面里可调的少数几个基础色，其余颜色由 <see cref="Palette"/> 派生。
/// 例如 <c>Palette.Apply(new PaletteSeed { Neutral = Colors.SteelBlue })</c> 即可在运行时换色。
/// </summary>
public sealed record PaletteSeed
{
    /// <summary>中性基色：卡片边框与图片占位由它去饱和得到。</summary>
    public Color Neutral { get; init; } = Color.Parse("#808080");

    /// <summary>浅色主题的卡片面。</summary>
    public Color CardSurface { get; init; } = Colors.White;

    /// <summary>浅色主题的卡片边框。</summary>
    public Color CardBorder { get; init; } = Color.Parse("#E4E4E7");

    /// <summary>强调色；留空表示跟随系统主题色。</summary>
    public Color? Accent { get; init; }
}
