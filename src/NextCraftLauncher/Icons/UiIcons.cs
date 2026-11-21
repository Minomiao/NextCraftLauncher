using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using NextCraftLauncher.Core.Logging;

namespace NextCraftLauncher.Icons;

/// <summary>一个图标解析后的结果：合并后的几何、墨水范围、viewBox、是否实心。</summary>
public sealed record UiIconGeometry(Geometry Geometry, Rect InkBounds, Rect ViewBox, bool Filled);

/// <summary>
/// 界面图标库。图标以 SVG 文件放在 <c>Assets/Icons/Ui/</c>，文件名即图标名；
/// 用的时候写 <c>&lt;controls:IconView Icon="close" /&gt;</c> 即可，加图标不需要改代码。
/// <para>
/// 解析规则：读出 <c>viewBox</c> 与根节点的 <c>fill</c>，把文件里所有 <c>path</c> 合成一个几何；
/// 线宽、颜色、圆头圆角由控件样式决定，SVG 里这些属性只是给编辑器和预览看的。
/// </para>
/// </summary>
public static class UiIcons
{
    private const string Root = "avares://NextCraftLauncher/Assets/Icons/Ui/";

    private static readonly Dictionary<string, UiIconGeometry?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static UiIconGeometry? For(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;
        }

        var loaded = Load(name.Trim());

        lock (Cache)
        {
            Cache[name] = loaded;
        }

        return loaded;
    }

    private static UiIconGeometry? Load(string name)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri($"{Root}{name}.svg"));
            var root = XDocument.Load(stream).Root;
            if (root is null) return null;

            var group = new GeometryGroup();

            foreach (var element in root.Descendants().Where(e => e.Name.LocalName == "path"))
            {
                var data = (string?)element.Attribute("d");
                if (string.IsNullOrWhiteSpace(data)) continue;

                try
                {
                    group.Children.Add(Geometry.Parse(data));
                }
                catch (FormatException)
                {
                    LauncherLog.Warn($"界面图标路径无效：{name}.svg");
                }
            }

            if (group.Children.Count == 0) return null;

            var ink = group.Bounds;
            var viewBox = ParseViewBox((string?)root.Attribute("viewBox")) ?? ink;

            // 根节点显式写了 fill 且不是 none，就当实心图标处理。
            var fill = ((string?)root.Attribute("fill"))?.Trim();
            var filled = !string.IsNullOrEmpty(fill) && !string.Equals(fill, "none", StringComparison.OrdinalIgnoreCase);

            return new UiIconGeometry(group, ink, viewBox, filled);
        }
        catch (Exception exception)
        {
            LauncherLog.Warn($"界面图标读取失败（{name}.svg）：{exception.Message}");
            return null;
        }
    }

    private static Rect? ParseViewBox(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parts = text.Split(new[] { ' ', ',', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4) return null;

        var values = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                return null;
        }

        return new Rect(values[0], values[1], values[2], values[3]);
    }

    /// <summary>供调试用：当前已加载的图标名。</summary>
    public static IReadOnlyCollection<string> LoadedNames
    {
        get
        {
            lock (Cache) return Cache.Keys.ToArray();
        }
    }
}
