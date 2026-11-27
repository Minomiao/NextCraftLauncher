using System;
using Avalonia;
using Avalonia.Media;

namespace NextCraftLauncher.Theme;

/// <summary>
/// 文字规范：一个基准字号 + 一张角色系数表。
/// 视图只通过 <c>App.axaml</c> 里的语义类引用，不写字号 / 字重 / 透明度字面量。
/// 行高、换行、对齐属于布局规范，不在这里定义。
/// </summary>
public static class Typography
{
    /// <summary>内容字号以它为基准，各角色按系数缩放后四舍五入。</summary>
    public const double BaseSize = 13;

    /// <summary>角色名 → 相对基准的系数。</summary>
    private static readonly (string Name, double Factor)[] Sizes =
    {
        ("Micro", 0.77),       // 10：暂只给折叠箭头使用
        ("Caption", 0.85),     // 11
        ("Meta", 0.92),        // 12
        ("Body", 1.00),        // 13
        ("Section", 1.15),     // 15
        ("Title", 1.38),       // 18
        ("Display", 1.69),     // 22
        ("DeviceCode", 2.31),  // 30：微软登录设备码
        ("BrandSplash", 4.00), // 52：启动画面品牌字
    };

    public static void Register(Application application)
    {
        foreach (var (name, factor) in Sizes)
            application.Resources["FontSize" + name] = Math.Round(BaseSize * factor);

        application.Resources["FontWeightEmphasis"] = FontWeight.SemiBold;
        application.Resources["FontWeightStrong"] = FontWeight.Bold;

        application.Resources["TextOpacitySecondary"] = 0.56;
        application.Resources["TextOpacityLabel"] = 0.72;
        application.Resources["TextOpacityTertiary"] = 0.40;

        application.Resources["LetterSpacingBrand"] = 2.0;
        application.Resources["LetterSpacingGroup"] = 0.8;

        application.Resources["FontFamilyMono"] = new FontFamily("Consolas");
    }
}
