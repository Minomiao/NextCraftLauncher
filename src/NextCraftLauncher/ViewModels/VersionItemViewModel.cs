using System.IO;
using NextCraftLauncher.Core.Minecraft;

namespace NextCraftLauncher.ViewModels;

public sealed class VersionItemViewModel
{
    public VersionItemViewModel(VersionSummary summary)
    {
        Summary = summary;
    }

    public VersionSummary Summary { get; }

    public string Id => Summary.Id;

    public string DirectoryPath => Path.GetDirectoryName(Summary.JsonPath) ?? string.Empty;

    public string Subtitle => $"{(Summary.Loader ?? Summary.TypeLabel)} · Java {Summary.RequiredJava}";

    /// <summary>
    /// 启动页轮播里的版本名：长名字缩小字号，保证整名显示而不是被截断。
    /// </summary>
    public double TitleFontSize => Id.Length switch
    {
        <= 20 => 32,
        <= 24 => 29,
        <= 28 => 25,
        <= 33 => 22,
        _ => 19,
    };
}
