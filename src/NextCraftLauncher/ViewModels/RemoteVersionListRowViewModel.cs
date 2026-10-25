namespace NextCraftLauncher.ViewModels;

/// <summary>下载页版本列表的扁平行，用于虚拟化列表（分组标题或版本项）。</summary>
public sealed class RemoteVersionListRowViewModel
{
    private RemoteVersionListRowViewModel(
        string? groupTitle,
        string? groupCountText,
        RemoteVersionItemViewModel? version)
    {
        GroupTitle = groupTitle;
        GroupCountText = groupCountText;
        Version = version;
    }

    public string? GroupTitle { get; }

    public string? GroupCountText { get; }

    public RemoteVersionItemViewModel? Version { get; }

    public bool IsHeader => Version is null;

    public bool IsVersion => Version is not null;

    public static RemoteVersionListRowViewModel CreateHeader(string title, int count)
        => new(title, $"{count} 个", null);

    public static RemoteVersionListRowViewModel CreateVersion(RemoteVersionItemViewModel version)
        => new(null, null, version);
}
