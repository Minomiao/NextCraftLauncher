using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NextCraftLauncher.Download.Java;

namespace NextCraftLauncher.ViewModels;

public partial class RemoteJavaRuntimeItemViewModel : ObservableObject
{
    private readonly Func<RemoteJavaRuntimeItemViewModel, Task> _download;

    public RemoteJavaRuntimeItemViewModel(
        JavaRuntimeCatalogEntry entry,
        Func<RemoteJavaRuntimeItemViewModel, Task> download)
    {
        ComponentName = entry.ComponentName;
        VersionName = entry.VersionName;
        MajorVersion = entry.MajorVersion;
        Released = entry.Released;
        _download = download;

        DownloadCommand = new AsyncRelayCommand(() => _download(this));
    }

    public string ComponentName { get; }

    public string VersionName { get; }

    public int MajorVersion { get; }

    public DateTimeOffset? Released { get; }

    public IAsyncRelayCommand DownloadCommand { get; }

    [ObservableProperty] private bool _isInstalled;

    public string Subtitle
    {
        get
        {
            var version = string.IsNullOrWhiteSpace(VersionName) ? ComponentName : VersionName;
            return Released is DateTimeOffset time
                ? $"Java {MajorVersion} · {version} · {time.LocalDateTime:yyyy-MM-dd}"
                : $"Java {MajorVersion} · {version}";
        }
    }
}
