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
}
