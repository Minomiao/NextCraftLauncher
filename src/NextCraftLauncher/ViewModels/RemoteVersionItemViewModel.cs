using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NextCraftLauncher.Download.Minecraft;

namespace NextCraftLauncher.ViewModels;

public partial class RemoteVersionItemViewModel : ObservableObject
{
    public RemoteVersionItemViewModel(
        MinecraftVersionEntry entry,
        Action<RemoteVersionItemViewModel>? select = null)
    {
        Id = entry.Id;
        Type = entry.Type ?? "unknown";
        ReleaseTime = entry.ReleaseTime;
        Url = entry.Url ?? string.Empty;
        Entry = entry;
        SelectCommand = new RelayCommand(() => select?.Invoke(this));
    }

    public string Id { get; }

    public string Type { get; }

    public DateTimeOffset? ReleaseTime { get; }

    public string Url { get; }

    public MinecraftVersionEntry Entry { get; }

    public IRelayCommand SelectCommand { get; }

    [ObservableProperty] private bool _isInstalled;

    [ObservableProperty] private bool _isSelected;

    public string TypeLabel => Type switch
    {
        "release" => "正式版",
        "snapshot" => "快照",
        "old_beta" => "远古 Beta",
        "old_alpha" => "远古 Alpha",
        _ => Type,
    };

    public string Subtitle => ReleaseTime is DateTimeOffset time
        ? $"{TypeLabel} · {time.LocalDateTime:yyyy-MM-dd}"
        : TypeLabel;
}
