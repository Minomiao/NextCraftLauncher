using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using NextCraftLauncher.Core.Java;
using NextCraftLauncher.Core.Launch;
using NextCraftLauncher.Core.Logging;
using NextCraftLauncher.Core.Minecraft;
using NextCraftLauncher.Core.Settings;
using NextCraftLauncher.Download;
using NextCraftLauncher.Download.Java;
using NextCraftLauncher.Download.Loaders;
using NextCraftLauncher.Download.Minecraft;

namespace NextCraftLauncher.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _lifetime = new();

    private ResolvedVersion? _resolved;
    private int _detailsRevision;
    private Task _detailsTask = Task.CompletedTask;
    private readonly TransferSpeedMeter _versionSpeedMeter = new();
    private readonly TransferSpeedMeter _javaSpeedMeter = new();
    private CancellationTokenSource? _versionDownloadCts;
    private CancellationTokenSource? _javaDownloadCts;
    private CancellationTokenSource? _loaderVersionsCts;
    private int _loaderVersionsGeneration;
    private bool _loaderMirrorDirty;
    private string? _selectedLoaderKey;
    private string? _selectedLoaderVersion;
    private bool _versionNameEdited;
    private bool _updatingVersionName;
    private readonly LoaderVersionPrefetcher _loaderPrefetcher;
    private MinecraftDownloadStage _versionStage = MinecraftDownloadStage.Preparing;
    private int _versionCompletedFiles;
    private int _versionTotalFiles;
    private int _javaCompletedFiles;
    private int _javaTotalFiles;

    public MainViewModel()
    {
        _settings = AppSettings.Load();
        _minecraftDirectory = _settings.MinecraftDirectory;
        _playerName = _settings.PlayerName;
        _memoryMb = MemoryOptions.Contains(_settings.MemoryMb) ? _settings.MemoryMb : 4096;
        _selectedGameMirrorIndex = (int)_settings.GameMirror;
        _selectedForgeMirrorIndex = LoaderMirrorIndexFrom(_settings.ForgeMirror);
        _selectedFabricMirrorIndex = LoaderMirrorIndexFrom(_settings.FabricMirror);
        _selectedNeoForgeMirrorIndex = LoaderMirrorIndexFrom(_settings.NeoForgeMirror);
        _downloadVersionIsolated = _settings.DownloadVersionIsolated;

        foreach (var directory in _settings.GameDirectories)
            GameFolders.Add(new GameFolderItemViewModel(directory));

        _selectedGameFolder = GameFolders.FirstOrDefault(folder =>
                                  string.Equals(folder.Path, _minecraftDirectory, StringComparison.OrdinalIgnoreCase))
                              ?? GameFolders.FirstOrDefault();

        LoaderGroups.Add(new LoaderVersionGroupViewModel("forge", "Forge"));
        LoaderGroups.Add(new LoaderVersionGroupViewModel("fabric", "Fabric"));
        LoaderGroups.Add(new LoaderVersionGroupViewModel("neoforge", "NeoForge"));

        _ = RefreshAsync();
        _ = PrefetchRemoteVersionsAsync();

        // 后台慢慢补全各游戏版本的加载器列表，结果写入 NCL/cache/loaders。
        _loaderPrefetcher = new LoaderVersionPrefetcher(
            ResolvePrefetchMirror,
            isBusy: () => IsDownloadingVersion || IsDownloadingJava || IsRemoteVersionsLoading);
        _loaderPrefetcher.LoaderVersionsCached += OnLoaderVersionsCached;
        _ = RunLoaderPrefetcherAsync();
    }

    private async Task RunLoaderPrefetcherAsync()
    {
        try
        {
            await _loaderPrefetcher.RunAsync(_lifetime.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // 后台预取失败不影响正常使用，下次启动会继续。
        }
    }

    private void OnLoaderVersionsCached(string loader, string minecraftVersion, LoaderVersionResult result)
    {
        Dispatcher.UIThread.Post(() => ApplyCachedLoaderGroup(loader, minecraftVersion, result));
    }

    /// <summary>后台预取完成后，把结果直接填进当前打开且匹配的详情页分组。</summary>
    private void ApplyCachedLoaderGroup(string loader, string minecraftVersion, LoaderVersionResult result)
    {
        var item = SelectedRemoteVersion;
        if (!IsRemoteVersionDetailOpen || item is null ||
            !string.Equals(item.Id, minecraftVersion, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var group = LoaderGroups.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, loader, StringComparison.OrdinalIgnoreCase));
        if (group is null) return;

        ApplyLoaderGroupResult(group, result);
    }

    /// <summary>
    /// 把结果写入分组；内容没有变化时只更新标题行，避免刷新时列表跳动。
    /// </summary>
    private void ApplyLoaderGroupResult(LoaderVersionGroupViewModel group, LoaderVersionResult result)
    {
        var changed = !SameLoaders(result.Versions, group.Versions);

        if (changed)
        {
            group.Versions.Clear();
            foreach (var version in result.Versions)
            {
                group.Versions.Add(new LoaderVersionItemViewModel(
                    version.Version,
                    version.Tag,
                    group.Key,
                    SelectLoaderVersion,
                    IsLoaderSelected(group.Key, version.Version)));
            }

            // 之前选中的版本已从列表里消失时清除选择。
            if (IsLoaderSelected(group.Key, _selectedLoaderVersion) &&
                !group.Versions.Any(item =>
                    string.Equals(item.Version, _selectedLoaderVersion, StringComparison.Ordinal)))
            {
                ClearLoaderSelection();
            }
        }

        group.IsLoading = false;
        group.IsAvailable = group.Versions.Count > 0;
        group.ErrorText = result.Error;
        group.StatusText = result.Error is not null
            ? "获取失败"
            : group.Versions.Count > 0
                ? $"{group.Versions.Count} 个版本"
                : "无可用版本";

        UpdateLoaderGroupRows(group, changed);
    }

    private static bool SameLoaders(
        IReadOnlyList<LoaderVersion> incoming,
        IReadOnlyList<LoaderVersionItemViewModel> current)
    {
        if (incoming.Count != current.Count) return false;

        for (var index = 0; index < incoming.Count; index++)
        {
            if (!string.Equals(incoming[index].Version, current[index].Version, StringComparison.Ordinal) ||
                !string.Equals(incoming[index].Tag, current[index].Tag, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>启动后台预取远程版本清单；先展示上次缓存，再用网络结果更新。</summary>
    private async Task PrefetchRemoteVersionsAsync()
    {
        try
        {
            var cached = await Task.Run(MinecraftVersionCatalog.TryLoadCached, _lifetime.Token);
            if (cached is not null && !_remoteManifestLoaded)
                ApplyRemoteManifest(cached, markLoaded: false);

            await EnsureRemoteVersionsLoadedAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    public ObservableCollection<VersionItemViewModel> Versions { get; } = new();

    public ObservableCollection<GameFolderItemViewModel> GameFolders { get; } = new();

    public ObservableCollection<RemoteVersionItemViewModel> RemoteVersions { get; } = new();

    public RangeObservableCollection<RemoteVersionListRowViewModel> RemoteVersionRows { get; } = new();

    /// <summary>版本详情页里的加载器分组（Forge / Fabric / NeoForge）。</summary>
    public ObservableCollection<LoaderVersionGroupViewModel> LoaderGroups { get; } = new();

    /// <summary>版本详情页的扁平行列表，和游戏版本列表共用同一套行样式。</summary>
    public RangeObservableCollection<LoaderVersionListRowViewModel> LoaderVersionRows { get; } = new();

    public ObservableCollection<RemoteJavaRuntimeItemViewModel> AvailableJavaRuntimes { get; } = new();

    public IReadOnlyList<string> RemoteVersionTypes { get; } = new[] { "全部", "正式版", "快照", "远古" };

    private readonly List<RemoteVersionItemViewModel> _allRemoteVersions = new();
    private readonly List<RemoteJavaRuntimeItemViewModel> _allJavaRuntimes = new();
    private bool _remoteManifestLoaded;
    private int _gameMirrorGeneration;
    private bool _javaRuntimeCatalogLoaded;

    public ObservableCollection<JavaRuntime> JavaRuntimes { get; } = new();

    public ObservableCollection<int> MemoryOptions { get; } = new(new[] { 1024, 2048, 4096, 6144, 8192, 12288, 16384 });

    public IReadOnlyList<string> DownloadMirrors { get; } = new[]
    {
        "自动",
        "Mojang 官方",
        "Mojang CDN",
        "BMCLAPI",
    };

    /// <summary>加载器只有自身的官网源，Mojang CDN 与加载器无关。</summary>
    public IReadOnlyList<string> LoaderMirrorOptions { get; } = new[]
    {
        "自动",
        "官方",
        "BMCLAPI",
    };

    [ObservableProperty] private string _minecraftDirectory = string.Empty;
    [ObservableProperty] private VersionItemViewModel? _selectedVersion;
    [ObservableProperty] private GameFolderItemViewModel? _selectedGameFolder;
    [ObservableProperty] private JavaRuntime? _selectedJava;
    [ObservableProperty] private string _playerName = "Player";
    [ObservableProperty] private int _memoryMb = 4096;
    [ObservableProperty] private string _status = "就绪";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isLaunching;
    [ObservableProperty] private string _versionsCountText = "尚未扫描";
    [ObservableProperty] private string _versionDetails = "请选择左侧版本。";
    [ObservableProperty] private string? _launchWarning;
    [ObservableProperty] private bool _isVersionIsolated;
    [ObservableProperty] private bool _isDownloadingJava;
    [ObservableProperty] private string _remoteVersionSearch = string.Empty;
    [ObservableProperty] private string _remoteVersionType = "全部";
    [ObservableProperty] private string _remoteVersionsStatus = "尚未加载版本清单";
    [ObservableProperty] private bool _isRemoteVersionsLoading;
    [ObservableProperty] private RemoteVersionItemViewModel? _selectedRemoteVersion;
    [ObservableProperty] private bool _isRemoteVersionDetailOpen;
    [ObservableProperty] private bool _downloadVersionIsolated = true;
    [ObservableProperty] private bool _isDownloadingVersion;
    [ObservableProperty] private bool _isVersionInstallIndeterminate;
    [ObservableProperty] private string _versionName = string.Empty;
    [ObservableProperty] private double _versionDownloadPercent;
    [ObservableProperty] private string _versionDownloadText = string.Empty;
    [ObservableProperty] private string _versionTaskTitle = "版本安装";
    [ObservableProperty] private bool _isJavaRuntimeCatalogLoading;
    [ObservableProperty] private string _javaRuntimeCatalogStatus = "尚未加载 Java 运行时列表";
    [ObservableProperty] private AppPage _currentPage = AppPage.Launch;
    [ObservableProperty] private double _javaDownloadPercent;
    [ObservableProperty] private string _javaDownloadText = string.Empty;
    [ObservableProperty] private int _selectedGameMirrorIndex;
    [ObservableProperty] private int _selectedForgeMirrorIndex;
    [ObservableProperty] private int _selectedFabricMirrorIndex;
    [ObservableProperty] private int _selectedNeoForgeMirrorIndex;
    [ObservableProperty] private LaunchSection _currentLaunchSection = LaunchSection.Start;
    [ObservableProperty] private DownloadSection _currentDownloadSection = DownloadSection.Versions;
    [ObservableProperty] private bool _isVersionDeleteDialogOpen;
    [ObservableProperty] private string _versionDeleteDialogMessage = string.Empty;

    private DownloadSection _previousDownloadSection = DownloadSection.Versions;

    public bool IsLaunchPageSelected => CurrentPage == AppPage.Launch;

    public bool IsDownloadPageSelected => CurrentPage == AppPage.Download;

    public bool IsStartSectionSelected => CurrentLaunchSection == LaunchSection.Start;

    public bool IsVersionsSectionSelected => CurrentLaunchSection == LaunchSection.Versions;

    public bool IsSettingsSectionSelected => CurrentLaunchSection == LaunchSection.Settings;

    public bool IsDownloadVersionsSelected => CurrentDownloadSection == DownloadSection.Versions;

    public bool IsDownloadJavaSelected => CurrentDownloadSection == DownloadSection.Java;

    public bool IsDownloadSettingsSelected => CurrentDownloadSection == DownloadSection.Settings;

    public bool IsDownloadRunningSelected => CurrentDownloadSection == DownloadSection.Running;

    public bool HasActiveDownloads => IsDownloadingVersion || IsDownloadingJava;

    public bool ShowRemoteVersionsLoading => IsRemoteVersionsLoading && RemoteVersionRows.Count == 0;

    public string CurrentVersionName => SelectedVersion?.Id ?? "未选择版本";

    public string CurrentVersionSubtitle => SelectedVersion?.Subtitle ?? "在左侧「版本」中选择一个版本";

    public string CurrentJavaName => SelectedJava?.DisplayName ?? "未选择 Java";

    public string RequiredJavaDescription => _resolved is null
        ? "请选择版本"
        : _resolved.Merged.JavaVersion is { Component: { Length: > 0 } component }
            ? $"Java {_resolved.RequiredJavaMajor}+（{component}）"
            : $"Java {_resolved.RequiredJavaMajor}+（自动选择组件）";

    public bool HasLaunchWarning => !string.IsNullOrEmpty(LaunchWarning);

    public string EffectiveGameDirectory => _resolved is null
        ? "-"
        : IsVersionIsolated
            ? _resolved.VersionDirectory
            : _resolved.MinecraftDirectory;

    public bool CanLaunch =>
        !IsBusy &&
        !IsLaunching &&
        _resolved is { IsComplete: true } &&
        SelectedJava is not null &&
        SelectedJava.MajorVersion >= _resolved.RequiredJavaMajor;

    public bool CanDownloadJava =>
        !IsBusy &&
        !IsLaunching &&
        !IsDownloadingJava &&
        _resolved is not null &&
        _resolved.RequiredJavaMajor > 0;

    public bool CanDownloadSelectedVersion =>
        !IsBusy &&
        !IsDownloadingVersion &&
        SelectedRemoteVersion is not null;

    public bool CanDownloadSelected =>
        !IsBusy &&
        !IsLaunching &&
        !IsDownloadingVersion &&
        SelectedRemoteVersion is not null &&
        VersionNaming.Normalize(VersionName).Length > 0;

    public string PrimaryDownloadText =>
        _selectedLoaderKey is null || _selectedLoaderVersion is null
            ? "下载选中版本"
            : $"下载并安装 {GetLoaderDisplayName(_selectedLoaderKey)} {_selectedLoaderVersion}";

    public bool CanManageSelectedVersion =>
        !IsBusy &&
        !IsLaunching &&
        SelectedVersion is not null;

    public bool HasVersionDownloadProgress => IsDownloadingVersion || !string.IsNullOrEmpty(VersionDownloadText);

    public bool HasJavaDownloadProgress => IsDownloadingJava || !string.IsNullOrEmpty(JavaDownloadText);

    public double OverallDownloadPercent
    {
        get
        {
            var sum = 0.0;
            var count = 0;
            if (IsDownloadingVersion) { sum += VersionDownloadPercent; count++; }
            if (IsDownloadingJava) { sum += JavaDownloadPercent; count++; }
            return count == 0 ? 0 : sum / count;
        }
    }

    public string OverallDownloadPercentText => $"{OverallDownloadPercent:0.0} %";

    public string CurrentDownloadSpeedText
    {
        get
        {
            var megabytesPerSecond = 0.0;
            if (IsDownloadingVersion) megabytesPerSecond += _versionSpeedMeter.MegabytesPerSecond;
            if (IsDownloadingJava) megabytesPerSecond += _javaSpeedMeter.MegabytesPerSecond;
            return $"{megabytesPerSecond:0.00} MB/s";
        }
    }

    public int RemainingFilesCount
    {
        get
        {
            var remaining = 0;
            if (IsDownloadingVersion) remaining += Math.Max(0, _versionTotalFiles - _versionCompletedFiles);
            if (IsDownloadingJava) remaining += Math.Max(0, _javaTotalFiles - _javaCompletedFiles);
            return remaining;
        }
    }

    public string VersionDownloadPercentText =>
        IsVersionInstallIndeterminate ? "安装中…" : $"{VersionDownloadPercent:0}%";

    public bool IsVersionStagePreparing => _versionStage == MinecraftDownloadStage.Preparing;

    public bool IsVersionStageFiles => _versionStage == MinecraftDownloadStage.Files;

    public bool IsVersionStageFinalizing => _versionStage == MinecraftDownloadStage.Finalizing;

    public bool IsVersionStepInfoDone => _versionStage != MinecraftDownloadStage.Preparing;

    public bool IsVersionStepFilesDone => _versionStage == MinecraftDownloadStage.Finalizing;

    private void NotifyDownloadStats()
    {
        OnPropertyChanged(nameof(OverallDownloadPercent));
        OnPropertyChanged(nameof(OverallDownloadPercentText));
        OnPropertyChanged(nameof(CurrentDownloadSpeedText));
        OnPropertyChanged(nameof(RemainingFilesCount));
    }

    private void NotifyVersionStage()
    {
        OnPropertyChanged(nameof(IsVersionStagePreparing));
        OnPropertyChanged(nameof(IsVersionStageFiles));
        OnPropertyChanged(nameof(IsVersionStageFinalizing));
        OnPropertyChanged(nameof(IsVersionStepInfoDone));
        OnPropertyChanged(nameof(IsVersionStepFilesDone));
    }

    partial void OnVersionDownloadPercentChanged(double value)
        => OnPropertyChanged(nameof(VersionDownloadPercentText));

    partial void OnIsVersionInstallIndeterminateChanged(bool value)
        => OnPropertyChanged(nameof(VersionDownloadPercentText));

    partial void OnSelectedVersionChanged(VersionItemViewModel? value)
    {
        OnPropertyChanged(nameof(CurrentVersionName));
        OnPropertyChanged(nameof(CurrentVersionSubtitle));
        OnPropertyChanged(nameof(CanManageSelectedVersion));
        _detailsTask = LoadDetailsAsync(value);
    }

    partial void OnSelectedGameFolderChanged(GameFolderItemViewModel? value)
    {
        if (value is null) return;
        if (string.Equals(MinecraftDirectory, value.Path, StringComparison.OrdinalIgnoreCase)) return;

        MinecraftDirectory = value.Path;
        _ = RefreshAsync();
    }

    partial void OnSelectedJavaChanged(JavaRuntime? value)
    {
        _settings.JavaPath = value?.Path;
        _settings.Save();
        OnPropertyChanged(nameof(CurrentJavaName));
        UpdateLaunchWarning();
    }

    partial void OnCurrentLaunchSectionChanged(LaunchSection value)
    {
        OnPropertyChanged(nameof(IsStartSectionSelected));
        OnPropertyChanged(nameof(IsVersionsSectionSelected));
        OnPropertyChanged(nameof(IsSettingsSectionSelected));
    }

    partial void OnCurrentDownloadSectionChanged(DownloadSection value)
    {
        OnPropertyChanged(nameof(IsDownloadVersionsSelected));
        OnPropertyChanged(nameof(IsDownloadJavaSelected));
        OnPropertyChanged(nameof(IsDownloadSettingsSelected));
        OnPropertyChanged(nameof(IsDownloadRunningSelected));

        if (value == DownloadSection.Java)
            _ = EnsureJavaRuntimesLoadedAsync();

        if (value == DownloadSection.Versions && !_remoteManifestLoaded)
            _ = EnsureRemoteVersionsLoadedAsync();

        if (value == DownloadSection.Versions && _loaderMirrorDirty)
        {
            _loaderMirrorDirty = false;
            if (IsRemoteVersionDetailOpen && SelectedRemoteVersion is not null)
                _ = LoadLoaderVersionsAsync(SelectedRemoteVersion);
        }
    }

    partial void OnIsLaunchingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(CanDownloadJava));
        OnPropertyChanged(nameof(CanManageSelectedVersion));
        OnPropertyChanged(nameof(CanDownloadSelected));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(CanDownloadJava));
        OnPropertyChanged(nameof(CanDownloadSelectedVersion));
        OnPropertyChanged(nameof(CanManageSelectedVersion));
        OnPropertyChanged(nameof(CanDownloadSelected));
    }

    partial void OnIsDownloadingJavaChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDownloadJava));
        OnPropertyChanged(nameof(HasJavaDownloadProgress));
        OnPropertyChanged(nameof(HasActiveDownloads));
        NotifyDownloadStats();
    }

    partial void OnJavaDownloadTextChanged(string value)
        => OnPropertyChanged(nameof(HasJavaDownloadProgress));

    partial void OnCurrentPageChanged(AppPage value)
    {
        OnPropertyChanged(nameof(IsLaunchPageSelected));
        OnPropertyChanged(nameof(IsDownloadPageSelected));

        if (value == AppPage.Download)
        {
            if (HasActiveDownloads) EnterTaskSection();
            _ = EnsureRemoteVersionsLoadedAsync();
            _ = EnsureJavaRuntimesLoadedAsync();

            // 预取在启动时完成；若此处仍未完成，界面会显示“正在获取版本清单”。
            if (_remoteManifestLoaded && RemoteVersions.Count == 0 && _allRemoteVersions.Count > 0)
                ApplyRemoteVersionFilter();
        }
    }

    partial void OnRemoteVersionSearchChanged(string value) => ApplyRemoteVersionFilter();

    partial void OnRemoteVersionTypeChanged(string value) => ApplyRemoteVersionFilter();

    partial void OnIsRemoteVersionsLoadingChanged(bool value)
        => OnPropertyChanged(nameof(ShowRemoteVersionsLoading));

    partial void OnSelectedRemoteVersionChanged(RemoteVersionItemViewModel? value)
    {
        foreach (var item in _allRemoteVersions)
            item.IsSelected = ReferenceEquals(item, value);

        OnPropertyChanged(nameof(CanDownloadSelectedVersion));
        OnPropertyChanged(nameof(CanDownloadSelected));
        OnPropertyChanged(nameof(PrimaryDownloadText));
    }

    partial void OnVersionNameChanged(string value)
    {
        if (_updatingVersionName) return;

        _versionNameEdited = true;
        OnPropertyChanged(nameof(CanDownloadSelected));
    }

    partial void OnIsDownloadingVersionChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDownloadSelectedVersion));
        OnPropertyChanged(nameof(CanDownloadSelected));
        OnPropertyChanged(nameof(HasVersionDownloadProgress));
        OnPropertyChanged(nameof(HasActiveDownloads));
        NotifyDownloadStats();
    }

    partial void OnVersionDownloadTextChanged(string value)
        => OnPropertyChanged(nameof(HasVersionDownloadProgress));

    [RelayCommand]
    private void ShowLaunchPage() => CurrentPage = AppPage.Launch;

    [RelayCommand]
    private void ShowStartSection() => CurrentLaunchSection = LaunchSection.Start;

    [RelayCommand]
    private void ShowVersionsSection() => CurrentLaunchSection = LaunchSection.Versions;

    [RelayCommand]
    private void ShowSettingsSection() => CurrentLaunchSection = LaunchSection.Settings;

    [RelayCommand]
    private void ShowDownloadVersions() => CurrentDownloadSection = DownloadSection.Versions;

    [RelayCommand]
    private void ShowDownloadJava() => CurrentDownloadSection = DownloadSection.Java;

    [RelayCommand]
    private void ShowDownloadSettings() => CurrentDownloadSection = DownloadSection.Settings;

    [RelayCommand]
    private void ShowDownloadRunning() => EnterTaskSection();

    [RelayCommand]
    private void ReturnFromDownloadTasks() => CurrentDownloadSection = _previousDownloadSection;

    private void EnterTaskSection()
    {
        if (CurrentDownloadSection != DownloadSection.Running)
            _previousDownloadSection = CurrentDownloadSection;

        CurrentDownloadSection = DownloadSection.Running;
    }

    [RelayCommand]
    private void CancelVersionDownload() => _versionDownloadCts?.Cancel();

    [RelayCommand]
    private void CancelJavaDownload() => _javaDownloadCts?.Cancel();

    [RelayCommand]
    private void ShowDownloadPage() => CurrentPage = AppPage.Download;

    [RelayCommand]
    private void RequestVersionDelete()
    {
        if (SelectedVersion is null) return;

        VersionDeleteDialogMessage =
            $"确定要处理版本 “{SelectedVersion.Id}” 吗？\n\n" +
            $"{SelectedVersion.DirectoryPath}\n\n" +
            "完全删除会从磁盘移除整个版本文件夹，且无法恢复。";
        IsVersionDeleteDialogOpen = true;
    }

    [RelayCommand]
    private void CancelVersionDelete() => IsVersionDeleteDialogOpen = false;

    [RelayCommand]
    private void HideSelectedVersion()
    {
        var version = SelectedVersion;
        IsVersionDeleteDialogOpen = false;
        if (version is null) return;

        var directory = AppSettings.NormalizePath(version.DirectoryPath);
        if (directory is not null &&
            !_settings.HiddenVersions.Any(item =>
                string.Equals(item, directory, StringComparison.OrdinalIgnoreCase)))
        {
            _settings.HiddenVersions.Add(directory);
            _settings.Save();
        }

        Versions.Remove(version);
        VersionsCountText = $"共 {Versions.Count} 个版本";
        SelectedVersion = Versions.FirstOrDefault();
        Status = $"已隐藏版本 {version.Id}";
    }

    [RelayCommand]
    private async Task DeleteSelectedVersionAsync()
    {
        var version = SelectedVersion;
        IsVersionDeleteDialogOpen = false;
        if (version is null || IsBusy || IsLaunching) return;

        var directory = AppSettings.NormalizePath(version.DirectoryPath);
        var versionsRoot = AppSettings.NormalizePath(Path.Combine(MinecraftDirectory, "versions"));
        if (directory is null || versionsRoot is null ||
            !directory.StartsWith(versionsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            Status = "删除版本失败：路径无效";
            return;
        }

        IsBusy = true;
        var deleted = false;

        try
        {
            await Task.Run(() => Directory.Delete(directory, recursive: true), _lifetime.Token);
            deleted = true;
            Status = $"已删除版本 {version.Id}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            VersionDeleteDialogMessage =
                $"删除版本 “{version.Id}” 失败：{ex.Message}\n\n" +
                "可以重试，或选择「不显示」仅从列表中隐藏。";
            IsVersionDeleteDialogOpen = true;
        }
        finally
        {
            IsBusy = false;
        }

        if (deleted) await RefreshAsync();
    }

    partial void OnLaunchWarningChanged(string? value) => OnPropertyChanged(nameof(HasLaunchWarning));

    partial void OnIsVersionIsolatedChanged(bool value) => OnPropertyChanged(nameof(EffectiveGameDirectory));

    partial void OnMinecraftDirectoryChanged(string value)
    {
        _settings.MinecraftDirectory = value;
        _settings.Save();
    }

    /// <summary>添加一个游戏文件夹并切换过去。</summary>
    public void AddGameFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        string fullPath;
        try
        {
            fullPath = AppSettings.NormalizePath(path) ?? path.Trim();
        }
        catch (Exception)
        {
            fullPath = path.Trim();
        }

        var existing = GameFolders.FirstOrDefault(folder =>
            string.Equals(folder.Path, fullPath, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            existing = new GameFolderItemViewModel(fullPath);
            GameFolders.Add(existing);
        }

        if (!_settings.GameDirectories.Any(item =>
                string.Equals(item, fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            _settings.GameDirectories.Add(fullPath);
        }

        _settings.Save();

        MinecraftDirectory = fullPath;
        SelectedGameFolder = existing;
        _ = RefreshAsync();
    }

    /// <summary>确保当前扫描的游戏目录出现在文件夹列表中。</summary>
    private void EnsureGameFolderEntry(string path) => AddGameFolderEntry(path, select: true);

    private void AddGameFolderEntry(string path, bool select)
    {
        var normalized = AppSettings.NormalizePath(path);
        if (normalized is null) return;

        var existing = GameFolders.FirstOrDefault(folder =>
            string.Equals(folder.Path, normalized, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            existing = new GameFolderItemViewModel(normalized);
            GameFolders.Add(existing);
        }

        if (!_settings.GameDirectories.Any(item =>
                string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            _settings.GameDirectories.Add(normalized);
            _settings.Save();
        }

        if (select && !ReferenceEquals(SelectedGameFolder, existing))
            SelectedGameFolder = existing;
    }

    partial void OnMemoryMbChanged(int value)
    {
        _settings.MemoryMb = value;
        _settings.Save();
    }

    partial void OnPlayerNameChanged(string value)
    {
        _settings.PlayerName = value;
        _settings.Save();
    }

    partial void OnDownloadVersionIsolatedChanged(bool value)
    {
        _settings.DownloadVersionIsolated = value;
        _settings.Save();
    }

    partial void OnSelectedGameMirrorIndexChanged(int value)
    {
        if (value is < 0 or > 3) return;

        var mirror = (DownloadMirror)value;
        if (_settings.GameMirror == mirror) return;

        _settings.GameMirror = mirror;
        _settings.Save();

        // 回到「游戏版本」子页时会重新拉取清单；进行中的旧请求结果也作废。
        _gameMirrorGeneration++;
        _remoteManifestLoaded = false;
        _javaRuntimeCatalogLoaded = false;
        RemoteVersionsStatus = "游戏版本源已切换，正在重新获取...";
    }

    partial void OnSelectedForgeMirrorIndexChanged(int value)
    {
        UpdateLoaderMirror(value, _settings.ForgeMirror, mirror => _settings.ForgeMirror = mirror);
    }

    partial void OnSelectedFabricMirrorIndexChanged(int value)
    {
        UpdateLoaderMirror(value, _settings.FabricMirror, mirror => _settings.FabricMirror = mirror);
    }

    partial void OnSelectedNeoForgeMirrorIndexChanged(int value)
    {
        UpdateLoaderMirror(value, _settings.NeoForgeMirror, mirror => _settings.NeoForgeMirror = mirror);
    }

    private void UpdateLoaderMirror(int value, DownloadMirror current, Action<DownloadMirror> apply)
    {
        if (value is < 0 or > 2) return;

        var mirror = LoaderMirrorFromIndex(value);
        if (current == mirror) return;

        apply(mirror);
        _settings.Save();
        _loaderMirrorDirty = true;
    }

    private DownloadMirror ResolveGameMirror() => ToDownloadMirror(SelectedGameMirrorIndex);

    private DownloadMirror ResolveLoaderMirror(string loader) => loader.ToLowerInvariant() switch
    {
        "forge" => LoaderMirrorFromIndex(SelectedForgeMirrorIndex),
        "fabric" => LoaderMirrorFromIndex(SelectedFabricMirrorIndex),
        "neoforge" => LoaderMirrorFromIndex(SelectedNeoForgeMirrorIndex),
        _ => DownloadMirror.Automatic,
    };

    private DownloadMirror ResolvePrefetchMirror(string key)
        => string.Equals(key, "game", StringComparison.OrdinalIgnoreCase)
            ? ResolveGameMirror()
            : ResolveLoaderMirror(key);

    private static DownloadMirror ToDownloadMirror(int index)
        => index is >= 0 and <= 3 ? (DownloadMirror)index : DownloadMirror.Automatic;

    private static int LoaderMirrorIndexFrom(DownloadMirror mirror) => mirror switch
    {
        DownloadMirror.BmclApi => 2,
        DownloadMirror.Automatic => 0,
        // 加载器没有 Mojang CDN / 官方之分，旧配置里的 MojangCdn 归入「官方」。
        _ => 1,
    };

    private static DownloadMirror LoaderMirrorFromIndex(int index) => index switch
    {
        1 => DownloadMirror.Official,
        2 => DownloadMirror.BmclApi,
        _ => DownloadMirror.Automatic,
    };

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        Status = "正在扫描版本...";

        try
        {
            var directory = MinecraftDirectory;
            var summaries = await Task.Run(() => VersionScanner.Scan(directory), _lifetime.Token);

            Versions.Clear();
            foreach (var summary in summaries)
            {
                var versionDirectory = AppSettings.NormalizePath(Path.GetDirectoryName(summary.JsonPath));
                if (versionDirectory is not null &&
                    _settings.HiddenVersions.Any(item =>
                        string.Equals(item, versionDirectory, StringComparison.OrdinalIgnoreCase)))
                    continue;

                Versions.Add(new VersionItemViewModel(summary));
            }

            VersionsCountText = $"共 {Versions.Count} 个版本";
            EnsureGameFolderEntry(directory);
            UpdateRemoteInstalledStatus();
            UpdateJavaRuntimeInstalledStatus();

            var previousId = SelectedVersion?.Id;
            SelectedVersion = Versions.FirstOrDefault(version => version.Id == previousId) ?? Versions.FirstOrDefault();

            await RefreshJavaAsync();

            Status = Versions.Count == 0
                ? "未找到版本，请确认路径指向 .minecraft 目录"
                : $"已加载 {Versions.Count} 个版本";

        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            Status = "扫描失败";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 从用户选择的 PCL2 启动器 exe、PCL 配置文件夹或游戏目录导入设置。
    /// </summary>
    public async Task ImportPcl2FromLocationAsync(string selectedPath)
    {
        if (IsBusy) return;

        try
        {
            var location = await Task.Run(
                () => Pcl2ConfigImporter.ResolveLocation(selectedPath),
                _lifetime.Token);

            if (location is null)
            {
                Status = "未找到 PCL2 配置";
                return;
            }

            MinecraftDirectory = location.GameDirectory;

            // PCL 中打开过的文件夹全部登记，最后仍停留在 PCL 当前的文件夹上。
            foreach (var directory in location.GameDirectories)
                AddGameFolderEntry(directory, select: false);

            var currentFolder = GameFolders.FirstOrDefault(folder =>
                string.Equals(folder.Path, location.GameDirectory, StringComparison.OrdinalIgnoreCase));
            if (currentFolder is not null) SelectedGameFolder = currentFolder;

            await RefreshAsync();

            if (!string.IsNullOrWhiteSpace(location.PreferredVersionId))
            {
                var preferred = Versions.FirstOrDefault(version =>
                    string.Equals(version.Id, location.PreferredVersionId, StringComparison.OrdinalIgnoreCase));
                if (preferred is not null) SelectedVersion = preferred;
            }

            // 等版本详情解析完，避免它随后覆盖导入的 Java 选择和隔离状态。
            await _detailsTask;

            var result = await Task.Run(
                () => Pcl2ConfigImporter.Import(location, SelectedVersion?.Id),
                _lifetime.Token);

            await ApplyPcl2ImportAsync(result, location.GameDirectories.Count);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            Status = "导入 PCL2 设置失败";
        }
    }

    private async Task ApplyPcl2ImportAsync(Pcl2ImportResult result, int folderCount = 0)
    {
        if (!result.FoundConfig)
        {
            Status = "未找到 PCL2 配置";
            return;
        }

        var applied = new List<string>();

        if (folderCount > 0)
            applied.Add($"{folderCount} 个文件夹");

        if (result.MemoryMb is int memoryMb)
        {
            if (!MemoryOptions.Contains(memoryMb))
            {
                MemoryOptions.Add(memoryMb);
                var sorted = MemoryOptions.OrderBy(value => value).ToList();
                MemoryOptions.Clear();
                foreach (var value in sorted) MemoryOptions.Add(value);
            }

            MemoryMb = memoryMb;
            applied.Add($"内存 {memoryMb} MB");
        }

        if (result.Isolated is bool isolated)
        {
            IsVersionIsolated = isolated;
            applied.Add(isolated ? "版本隔离开" : "版本隔离关");
        }

        if (!string.IsNullOrWhiteSpace(result.JavaPath))
        {
            var java = JavaRuntimes.FirstOrDefault(runtime =>
                string.Equals(runtime.Path, result.JavaPath, StringComparison.OrdinalIgnoreCase));

            if (java is null)
            {
                java = await JavaLocator.ProbeAsync(result.JavaPath, MinecraftDirectory, _lifetime.Token);
                if (java is not null) JavaRuntimes.Add(java);
            }

            if (java is not null)
            {
                SelectedJava = java;
                applied.Add($"Java {java.MajorVersion}");
            }
        }
        else if (!string.IsNullOrWhiteSpace(result.JavaRange))
        {
            var java = JavaLocator.SelectInRange(JavaRuntimes, result.JavaRange);
            if (java is not null)
            {
                SelectedJava = java;
                applied.Add($"Java {java.MajorVersion}");
            }
        }

        Status = applied.Count > 0
            ? "已导入 PCL2 设置：" + string.Join("、", applied)
            : "已读取 PCL2 配置，但没有可应用的启动设置";
    }

    [RelayCommand]
    private async Task LoadRemoteVersionsAsync()
    {
        if (IsRemoteVersionsLoading) return;

        IsRemoteVersionsLoading = true;
        RemoteVersionsStatus = "正在获取 Mojang 版本清单...";
        var generation = _gameMirrorGeneration;

        try
        {
            // 清单请求不等待测速：多个候选源直接并发竞速。
            var mirror = ResolveGameMirror();
            var result = await MinecraftVersionCatalog.LoadAsync(
                mirror: mirror,
                cancellationToken: _lifetime.Token);

            // 加载期间切换了游戏版本源，丢弃这次结果，回到该子页时会重新获取。
            if (generation != _gameMirrorGeneration)
            {
                _remoteManifestLoaded = false;
                return;
            }

            ApplyRemoteManifest(result, markLoaded: true);
            Status = $"已加载 {_allRemoteVersions.Count} 个远程版本（最新正式版 {result.LatestRelease ?? "-"}）";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            RemoteVersionsStatus = "获取版本清单失败：" + exception.Message;
        }
        finally
        {
            IsRemoteVersionsLoading = false;
        }
    }

    private void ApplyRemoteManifest(MinecraftVersionManifestResult result, bool markLoaded)
    {
        var selectedId = SelectedRemoteVersion?.Id;

        _allRemoteVersions.Clear();
        foreach (var entry in result.Versions)
            _allRemoteVersions.Add(new RemoteVersionItemViewModel(entry, SelectRemoteVersion));

        if (markLoaded) _remoteManifestLoaded = true;

        // 重新构建集合后把选中的版本重新指向新实例，避免详情页引用旧对象。
        if (selectedId is not null)
        {
            var selected = _allRemoteVersions.FirstOrDefault(item =>
                string.Equals(item.Id, selectedId, StringComparison.OrdinalIgnoreCase));

            if (selected is not null)
                SelectedRemoteVersion = selected;
            else if (IsRemoteVersionDetailOpen)
                IsRemoteVersionDetailOpen = false;
        }

        UpdateRemoteInstalledStatus();
        ApplyRemoteVersionFilter();
    }

    private async Task EnsureRemoteVersionsLoadedAsync()
    {
        if (_remoteManifestLoaded || IsRemoteVersionsLoading) return;
        await LoadRemoteVersionsAsync();
    }

    private void ApplyRemoteVersionFilter()
    {
        RemoteVersions.Clear();
        var rows = new List<RemoteVersionListRowViewModel>();

        var search = RemoteVersionSearch.Trim();
        var filtered = new List<RemoteVersionItemViewModel>();

        foreach (var item in _allRemoteVersions)
        {
            if (!MatchesRemoteVersionType(item)) continue;
            if (search.Length > 0 && !item.Id.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            filtered.Add(item);
        }

        foreach (var group in filtered
                     .GroupBy(item => item.Type)
                     .OrderBy(group => VersionTypeOrder(group.Key)))
        {
            var items = group
                .OrderByDescending(item => item.ReleaseTime ?? DateTimeOffset.MinValue)
                .ToList();

            rows.Add(RemoteVersionListRowViewModel.CreateHeader(VersionTypeTitle(group.Key), items.Count));
            foreach (var item in items)
            {
                RemoteVersions.Add(item);
                rows.Add(RemoteVersionListRowViewModel.CreateVersion(item));
            }
        }

        RemoteVersionRows.ReplaceAll(rows);
        OnPropertyChanged(nameof(ShowRemoteVersionsLoading));
        UpdateRemoteVersionsStatus();
    }

    private void SelectRemoteVersion(RemoteVersionItemViewModel item)
    {
        SelectedRemoteVersion = item;
        IsRemoteVersionDetailOpen = true;

        _selectedLoaderKey = null;
        _selectedLoaderVersion = null;
        _versionNameEdited = false;
        SetVersionName(VersionNaming.BuildDefault(item.Id, null, null));

        _ = LoadLoaderVersionsAsync(item);
    }

    [RelayCommand]
    private void CloseRemoteVersionDetail()
    {
        _loaderVersionsCts?.Cancel();
        IsRemoteVersionDetailOpen = false;
    }

    private void ResetLoaderGroups()
    {
        ClearLoaderSelection();

        foreach (var group in LoaderGroups)
        {
            group.Versions.Clear();
            group.IsAvailable = false;
            group.IsExpanded = false;
            group.IsLoading = true;
            group.ErrorText = null;
            group.StatusText = "加载中…";
        }

        RebuildLoaderVersionRows();
    }

    /// <summary>
    /// 把分组与展开状态展平成列表行：分组标题始终显示，展开后显示版本行或空占位。
    /// </summary>
    private void RebuildLoaderVersionRows()
    {
        var rows = new List<LoaderVersionListRowViewModel>();

        foreach (var group in LoaderGroups)
        {
            rows.Add(CreateLoaderHeaderRow(group));

            if (!group.IsExpanded) continue;

            if (group.Versions.Count == 0)
            {
                rows.Add(LoaderVersionListRowViewModel.CreateEmpty(group, DescribeLoaderEmpty(group)));
                continue;
            }

            foreach (var version in group.Versions)
                rows.Add(LoaderVersionListRowViewModel.CreateVersion(version));
        }

        LoaderVersionRows.ReplaceAll(rows);
    }

    private LoaderVersionListRowViewModel CreateLoaderHeaderRow(LoaderVersionGroupViewModel group)
        => LoaderVersionListRowViewModel.CreateHeader(group, () => ToggleLoaderGroup(group));

    private static string DescribeLoaderEmpty(LoaderVersionGroupViewModel group)
    {
        if (group.IsLoading) return "正在获取版本列表…";
        if (!string.IsNullOrEmpty(group.ErrorText)) return $"获取失败：{group.ErrorText}";
        return $"该版本没有可用的 {group.DisplayName} 版本";
    }

    /// <summary>
    /// 只增删该分组的内容行，不重建整个列表，避免切换展开时滚动位置跳回顶部。
    /// </summary>
    private void UpdateLoaderGroupRows(LoaderVersionGroupViewModel group, bool contentChanged = true)
    {
        var headerIndex = -1;
        for (var index = 0; index < LoaderVersionRows.Count; index++)
        {
            if (LoaderVersionRows[index].IsHeader &&
                ReferenceEquals(LoaderVersionRows[index].Group, group))
            {
                headerIndex = index;
                break;
            }
        }

        if (headerIndex < 0)
        {
            RebuildLoaderVersionRows();
            return;
        }

        LoaderVersionRows[headerIndex] = CreateLoaderHeaderRow(group);

        // 后台刷新拿到与当前相同的数据时只换标题，不动内容行。
        if (!contentChanged) return;

        var contentCount = 0;
        while (headerIndex + 1 + contentCount < LoaderVersionRows.Count &&
               !LoaderVersionRows[headerIndex + 1 + contentCount].IsHeader)
        {
            contentCount++;
        }

        if (contentCount > 0)
            LoaderVersionRows.RemoveRange(headerIndex + 1, contentCount);

        if (!group.IsExpanded) return;

        if (group.Versions.Count == 0)
        {
            LoaderVersionRows.Insert(
                headerIndex + 1,
                LoaderVersionListRowViewModel.CreateEmpty(group, DescribeLoaderEmpty(group)));
            return;
        }

        LoaderVersionRows.InsertRange(
            headerIndex + 1,
            group.Versions.Select(version =>
                LoaderVersionListRowViewModel.CreateVersion(version)));
    }

    private bool IsLoaderSelected(string? loaderKey, string? version)
        => loaderKey is not null &&
           version is not null &&
           string.Equals(_selectedLoaderKey, loaderKey, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(_selectedLoaderVersion, version, StringComparison.Ordinal);

    private void SelectLoaderVersion(LoaderVersionItemViewModel item)
    {
        // 加载器互不兼容：再次点击已选中的行即取消，只能保留一个选择。
        if (item.IsSelected)
        {
            ClearLoaderSelection();
            return;
        }

        _selectedLoaderKey = item.LoaderKey;
        _selectedLoaderVersion = item.Version;

        foreach (var group in LoaderGroups)
            foreach (var version in group.Versions)
                version.IsSelected = ReferenceEquals(version, item);

        NotifyLoaderSelectionChanged();
        RefreshDefaultVersionName();
    }

    private void ClearLoaderSelection()
    {
        _selectedLoaderKey = null;
        _selectedLoaderVersion = null;

        foreach (var group in LoaderGroups)
            foreach (var version in group.Versions)
                version.IsSelected = false;

        NotifyLoaderSelectionChanged();
        RefreshDefaultVersionName();
    }

    private void NotifyLoaderSelectionChanged()
    {
        OnPropertyChanged(nameof(CanDownloadSelected));
        OnPropertyChanged(nameof(PrimaryDownloadText));
    }

    private void RefreshDefaultVersionName()
    {
        if (_versionNameEdited) return;

        SetVersionName(VersionNaming.BuildDefault(
            SelectedRemoteVersion?.Id ?? string.Empty,
            _selectedLoaderKey,
            _selectedLoaderVersion));
    }

    private void SetVersionName(string value)
    {
        _updatingVersionName = true;
        VersionName = value;
        _updatingVersionName = false;
    }

    [RelayCommand]
    private void ResetVersionName()
    {
        _versionNameEdited = false;
        RefreshDefaultVersionName();
    }

    private static string GetLoaderDisplayName(string loader) => loader.ToLowerInvariant() switch
    {
        "forge" => "Forge",
        "fabric" => "Fabric",
        "neoforge" => "NeoForge",
        _ => loader,
    };

    private void ToggleLoaderGroup(LoaderVersionGroupViewModel group)
    {
        group.IsExpanded = !group.IsExpanded;
        UpdateLoaderGroupRows(group);
    }

    private async Task LoadLoaderVersionsAsync(RemoteVersionItemViewModel item)
    {
        _loaderVersionsCts?.Cancel();
        _loaderMirrorDirty = false;

        var generation = ++_loaderVersionsGeneration;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _loaderVersionsCts = cts;

        ResetLoaderGroups();

        try
        {
            await Task.WhenAll(LoaderGroups.Select(group =>
                LoadLoaderGroupAsync(group, item.Id, generation, cts.Token)));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task LoadLoaderGroupAsync(
        LoaderVersionGroupViewModel group,
        string minecraftVersion,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            // 1) 先用本地缓存（72 小时内）秒开，不等网络。
            if (LoaderVersionStore.TryLoad(group.Key, minecraftVersion, out var cached, out var cachedAt) &&
                LoaderVersionStore.IsFresh(cachedAt))
            {
                ApplyLoaderGroupResult(group, cached);
            }

            // 2) 无论是否有缓存，都继续访问网络获取最新数据（按分组各自的源）。
            var mirror = ResolveLoaderMirror(group.Key);
            var result = await LoaderVersionCatalog.LoadAsync(
                group.Key, minecraftVersion, mirror, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // 切换下载源或版本时旧请求可能刚好返回，丢弃过期结果。
            if (generation != _loaderVersionsGeneration) return;

            ApplyLoaderGroupResult(group, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (generation != _loaderVersionsGeneration) return;

            group.IsLoading = false;
            group.ErrorText = exception.Message;

            if (group.Versions.Count > 0)
            {
                // 已有缓存时保留内容，只提示本次刷新失败。
                group.IsAvailable = true;
                group.StatusText = $"{group.Versions.Count} 个版本";
                UpdateLoaderGroupRows(group, contentChanged: false);
            }
            else
            {
                group.IsAvailable = false;
                group.StatusText = "获取失败";
                UpdateLoaderGroupRows(group);
            }
        }
    }

    private static int VersionTypeOrder(string type) => type switch
    {
        "release" => 0,
        "snapshot" => 1,
        "old_beta" => 2,
        "old_alpha" => 3,
        _ => 4,
    };

    private static string VersionTypeTitle(string type) => type switch
    {
        "release" => "正式版",
        "snapshot" => "快照",
        "old_beta" => "远古 Beta",
        "old_alpha" => "远古 Alpha",
        _ => "其他",
    };

    private bool MatchesRemoteVersionType(RemoteVersionItemViewModel item) => RemoteVersionType switch
    {
        "正式版" => item.Type == "release",
        "快照" => item.Type == "snapshot",
        "远古" => item.Type is "old_beta" or "old_alpha",
        _ => true,
    };

    private void UpdateRemoteInstalledStatus()
    {
        if (_allRemoteVersions.Count == 0) return;

        var installed = new HashSet<string>(Versions.Select(version => version.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var item in _allRemoteVersions)
            item.IsInstalled = installed.Contains(item.Id);

        UpdateRemoteVersionsStatus();
    }

    private void UpdateRemoteVersionsStatus()
    {
        if (_allRemoteVersions.Count == 0)
        {
            if (!IsRemoteVersionsLoading) RemoteVersionsStatus = "尚未加载版本清单";
            return;
        }

        var installedCount = _allRemoteVersions.Count(item => item.IsInstalled);
        RemoteVersionsStatus = RemoteVersions.Count == _allRemoteVersions.Count
            ? $"共 {_allRemoteVersions.Count} 个版本 · 已安装 {installedCount} 个"
            : $"显示 {RemoteVersions.Count} / {_allRemoteVersions.Count} 个版本 · 已安装 {installedCount} 个";
    }

    [RelayCommand]
    private async Task DownloadSelectedAsync()
    {
        if (!CanDownloadSelected || SelectedRemoteVersion is null) return;

        if (_selectedLoaderKey is not null && _selectedLoaderVersion is not null)
            await InstallSelectedLoaderAsync();
        else
            await DownloadSelectedVersionAsync();
    }

    /// <summary>校验并规范化版本名；名称冲突时直接给出提示。</summary>
    private bool TryResolveVersionName(
        string minecraftVersion,
        bool loaderSelected,
        out string name)
    {
        name = VersionNaming.Normalize(VersionName);

        if (name.Length == 0)
        {
            Status = "版本名不能为空";
            return false;
        }

        var isDefaultVanilla = !loaderSelected &&
                               string.Equals(name, minecraftVersion, StringComparison.OrdinalIgnoreCase);

        if (!isDefaultVanilla &&
            Directory.Exists(Path.Combine(MinecraftDirectory, "versions", name)))
        {
            Status = $"版本名 “{name}” 已存在，请换一个名称";
            return false;
        }

        return true;
    }

    [RelayCommand]
    private async Task DownloadSelectedVersionAsync()
    {
        if (!CanDownloadSelectedVersion || SelectedRemoteVersion is null) return;

        var item = SelectedRemoteVersion;

        if (!TryResolveVersionName(item.Id, loaderSelected: false, out var desiredName))
            return;

        using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _versionDownloadCts = downloadCts;

        IsDownloadingVersion = true;
        CurrentPage = AppPage.Download;
        EnterTaskSection();
        VersionTaskTitle = desiredName;
        VersionDownloadPercent = 0;
        VersionDownloadText = "正在准备下载...";
        IsVersionInstallIndeterminate = false;
        _versionStage = MinecraftDownloadStage.Preparing;
        _versionCompletedFiles = 0;
        _versionTotalFiles = 0;
        _versionSpeedMeter.Reset();
        NotifyVersionStage();
        NotifyDownloadStats();
        Status = $"正在下载 Minecraft {item.Id}...";
        LauncherLog.Info($"开始下载版本 {item.Id}（目标名称 {desiredName}）");

        try
        {
            // 版本列表与文件下载共用「游戏版本」源。
            var mirror = ResolveGameMirror();

            var progress = new Progress<MinecraftVersionDownloadProgress>(report =>
            {
                if (_versionStage != report.Stage)
                {
                    _versionStage = report.Stage;
                    NotifyVersionStage();
                }

                _versionCompletedFiles = report.CompletedFiles;
                _versionTotalFiles = report.TotalFiles;

                var percent = report.Percent is double value ? $"（{value:0}%）" : string.Empty;
                var megabytes = report.BytesDownloaded / 1024.0 / 1024.0;
                var speed = _versionSpeedMeter.Feed(report.BytesDownloaded);
                var speedText = speed.Length > 0 ? $"，{speed}" : string.Empty;

                // 并发下载时进度可能因文件完成顺序出现瞬时回落，进度条只允许前进。
                if (report.Percent is double currentPercent && currentPercent > VersionDownloadPercent)
                    VersionDownloadPercent = currentPercent;

                VersionDownloadText = $"{report.CompletedFiles}/{report.TotalFiles} 个文件，{megabytes:0.0} MB{speedText}";
                Status = report.TotalFiles > 0
                    ? $"正在下载 {item.Id}：{report.CompletedFiles}/{report.TotalFiles} 个文件，{megabytes:0.0} MB{speedText}{percent}"
                    : $"正在下载 {item.Id}...";
                NotifyDownloadStats();
            });

            var result = await MinecraftVersionDownloader.DownloadAsync(
                MinecraftDirectory,
                item.Entry,
                DownloadVersionIsolated,
                progress,
                new MinecraftVersionDownloadOptions
                {
                    Mirror = mirror,
                },
                downloadCts.Token);

            VersionDownloadPercent = 100;
            VersionDownloadText = "下载完成";

            var finalId = VersionNaming.RenameInstalledVersion(
                MinecraftDirectory, result.VersionId, desiredName);

            await RefreshAsync();

            var local = Versions.FirstOrDefault(version =>
                string.Equals(version.Id, finalId, StringComparison.OrdinalIgnoreCase));
            if (local is not null) SelectedVersion = local;

            UpdateRemoteInstalledStatus();
            Status = $"版本 {finalId} 下载完成";
            LauncherLog.Info($"版本 {finalId} 下载完成");
        }
        catch (OperationCanceledException)
        {
            Status = "版本下载已取消";
            VersionDownloadText = "下载已取消";
            LauncherLog.Warn($"版本 {item.Id} 下载已取消");
        }
        catch (Exception exception)
        {
            Status = "版本下载失败";
            VersionDownloadText = "下载失败";
            LauncherLog.Error($"版本 {item.Id} 下载失败", exception);
        }
        finally
        {
            IsDownloadingVersion = false;
            IsVersionInstallIndeterminate = false;
            if (ReferenceEquals(_versionDownloadCts, downloadCts)) _versionDownloadCts = null;
        }
    }

    [RelayCommand]
    private async Task InstallSelectedLoaderAsync()
    {
        if (SelectedRemoteVersion is null ||
            _selectedLoaderKey is null ||
            _selectedLoaderVersion is null ||
            IsDownloadingVersion || IsBusy || IsLaunching)
        {
            return;
        }

        var item = SelectedRemoteVersion;
        var loader = _selectedLoaderKey;
        var loaderVersion = _selectedLoaderVersion;
        var displayName = GetLoaderDisplayName(loader);

        if (!TryResolveVersionName(item.Id, loaderSelected: true, out var desiredName))
            return;

        using var installCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _versionDownloadCts = installCts;

        IsDownloadingVersion = true;
        CurrentPage = AppPage.Download;
        EnterTaskSection();
        VersionTaskTitle = desiredName;
        VersionDownloadPercent = 0;
        VersionDownloadText = "正在准备安装...";
        IsVersionInstallIndeterminate = false;
        _versionStage = MinecraftDownloadStage.Preparing;
        _versionCompletedFiles = 0;
        _versionTotalFiles = 0;
        _versionSpeedMeter.Reset();
        NotifyVersionStage();
        NotifyDownloadStats();
        Status = $"正在安装 {displayName} {loaderVersion}...";
        LauncherLog.Info($"开始安装 {displayName} {loaderVersion} → {desiredName}");

        // 原版下载占进度条前 65%，加载器安装占剩余部分，整个流程一条进度不重置。
        var vanillaJson = Path.Combine(
            MinecraftDirectory, "versions", item.Id, item.Id + ".json");
        var needsVanilla = !File.Exists(vanillaJson);
        var vanillaPhase = needsVanilla ? 65.0 : 0.0;
        var installerRunning = false;

        var creepTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        creepTimer.Tick += (_, _) =>
        {
            if (!installerRunning) return;
            if (VersionDownloadPercent < 95)
                VersionDownloadPercent = Math.Min(95, VersionDownloadPercent + 0.4);
        };
        creepTimer.Start();

        try
        {
            if (needsVanilla)
            {
                var vanillaProgress = new Progress<MinecraftVersionDownloadProgress>(report =>
                {
                    if (_versionStage != report.Stage)
                    {
                        _versionStage = report.Stage;
                        NotifyVersionStage();
                    }

                    if (report.Percent is double percent)
                    {
                        var mapped = percent * vanillaPhase / 100.0;
                        if (mapped > VersionDownloadPercent) VersionDownloadPercent = mapped;
                    }

                    VersionDownloadText = report.TotalFiles > 0
                        ? $"正在下载原版文件 {report.CompletedFiles}/{report.TotalFiles}..."
                        : "正在下载原版文件...";
                    NotifyDownloadStats();
                });

                await MinecraftVersionDownloader.DownloadAsync(
                    MinecraftDirectory,
                    item.Entry,
                    DownloadVersionIsolated,
                    vanillaProgress,
                    new MinecraftVersionDownloadOptions { Mirror = ResolveGameMirror() },
                    installCts.Token);
            }

            string? javaPath = null;

            if (loader is "forge" or "neoforge")
            {
                var requiredJava = ReadRequiredJavaMajor(item.Id, loader);
                var runtimes = JavaRuntimes.Count > 0
                    ? (IEnumerable<JavaRuntime>)JavaRuntimes
                    : await JavaLocator.DiscoverAsync(
                        MinecraftDirectory, _settings.JavaPath, installCts.Token);

                var java = SelectedJava is not null && SelectedJava.MajorVersion >= requiredJava
                    ? SelectedJava
                    : JavaLocator.SelectBest(runtimes, requiredJava);

                if (java is null)
                {
                    throw new InvalidOperationException(
                        $"未找到可运行 {displayName} 安装器的 Java（需要 Java {requiredJava}+）。");
                }

                javaPath = java.Path;
            }

            var installProgress = new Progress<LoaderInstallProgress>(report =>
            {
                if (_versionStage != report.Stage)
                {
                    _versionStage = report.Stage;
                    NotifyVersionStage();
                }

                if (report.Percent is double percent)
                {
                    installerRunning = false;
                    var mapped = vanillaPhase + percent * (100 - vanillaPhase) / 100.0;
                    if (mapped > VersionDownloadPercent) VersionDownloadPercent = mapped;
                }
                else
                {
                    // Forge / NeoForge 安装器不提供百分比，由定时器推进进度条。
                    installerRunning = true;
                }

                IsVersionInstallIndeterminate = false;

                VersionDownloadText = report.Text;
                Status = report.Text;
                NotifyDownloadStats();
            });

            // 安装器输出很密集，限制刷新频率，只展示最近一行。
            var lastLogTicks = 0L;
            var installLog = new Progress<string>(line =>
            {
                var now = Environment.TickCount64;
                if (now - lastLogTicks < 200) return;
                lastLogTicks = now;

                VersionDownloadText = line;
                Status = line;
            });

            var result = await LoaderInstaller.InstallAsync(
                MinecraftDirectory,
                item.Id,
                loader,
                loaderVersion,
                new LoaderInstallOptions
                {
                    Mirror = ResolveLoaderMirror(loader),
                    JavaPath = javaPath,
                    VersionName = desiredName,
                    Progress = installProgress,
                    Log = installLog,
                },
                installCts.Token);

            VersionDownloadPercent = 100;
            VersionDownloadText = "安装完成";

            await RefreshAsync();

            var local = Versions.FirstOrDefault(version =>
                string.Equals(version.Id, result.VersionId, StringComparison.OrdinalIgnoreCase));
            if (local is not null) SelectedVersion = local;

            Status = $"{displayName} {loaderVersion} 安装完成";
            LauncherLog.Info($"{displayName} {loaderVersion} 安装完成 → {result.VersionId}");
        }
        catch (OperationCanceledException)
        {
            Status = "加载器安装已取消";
            VersionDownloadText = "安装已取消";
            LauncherLog.Warn($"{displayName} {loaderVersion} 安装已取消");
        }
        catch (Exception exception)
        {
            Status = $"加载器安装失败：{exception.Message}";
            VersionDownloadText = "安装失败";
            LauncherLog.Error($"{displayName} {loaderVersion} 安装失败", exception);
        }
        finally
        {
            creepTimer.Stop();
            IsDownloadingVersion = false;
            IsVersionInstallIndeterminate = false;
            if (ReferenceEquals(_versionDownloadCts, installCts)) _versionDownloadCts = null;
        }
    }

    /// <summary>读取原版 JSON 中要求的 Java 主版本；Forge 至少 8，NeoForge 至少 17。</summary>
    private int ReadRequiredJavaMajor(string minecraftVersion, string loader)
    {
        var fallback = loader == "neoforge" ? 17 : 8;

        try
        {
            var path = Path.Combine(
                MinecraftDirectory, "versions", minecraftVersion, minecraftVersion + ".json");
            if (!File.Exists(path)) return fallback;

            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.TryGetProperty("javaVersion", out var javaVersion) &&
                javaVersion.TryGetProperty("majorVersion", out var majorVersion))
            {
                var required = majorVersion.GetInt32();
                return loader == "neoforge" ? Math.Max(required, 17) : Math.Max(required, 8);
            }
        }
        catch (Exception)
        {
            // 解析失败时使用兜底值。
        }

        return fallback;
    }

    [RelayCommand]
    private async Task LoadJavaRuntimesAsync()
    {
        if (IsJavaRuntimeCatalogLoading) return;

        IsJavaRuntimeCatalogLoading = true;
        JavaRuntimeCatalogStatus = "正在获取 Mojang Java 运行时列表...";

        try
        {
            var mirror = ResolveGameMirror();
            var result = await JavaRuntimeCatalog.LoadAsync(
                mirror: mirror,
                cancellationToken: _lifetime.Token);

            _allJavaRuntimes.Clear();
            foreach (var entry in result.Runtimes)
                _allJavaRuntimes.Add(new RemoteJavaRuntimeItemViewModel(entry, DownloadCatalogJavaAsync));

            AvailableJavaRuntimes.Clear();
            foreach (var item in _allJavaRuntimes)
                AvailableJavaRuntimes.Add(item);

            _javaRuntimeCatalogLoaded = true;
            UpdateJavaRuntimeInstalledStatus();
            UpdateJavaRuntimeCatalogStatus();

            Status = $"已加载 {_allJavaRuntimes.Count} 个 Java 运行时（{result.Platform}）";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            JavaRuntimeCatalogStatus = "获取 Java 运行时列表失败：" + exception.Message;
        }
        finally
        {
            IsJavaRuntimeCatalogLoading = false;
        }
    }

    private async Task EnsureJavaRuntimesLoadedAsync()
    {
        if (_javaRuntimeCatalogLoaded || IsJavaRuntimeCatalogLoading) return;
        await LoadJavaRuntimesAsync();
    }

    private async Task DownloadCatalogJavaAsync(RemoteJavaRuntimeItemViewModel item)
    {
        if (item.IsInstalled) return;

        var label = string.IsNullOrWhiteSpace(item.VersionName)
            ? item.ComponentName
            : $"Java {item.MajorVersion}（{item.VersionName}）";

        await DownloadJavaRuntimeAsync(item.ComponentName, item.MajorVersion, label);
    }

    private bool IsJavaRuntimeInstalled(string componentName)
    {
        var runtimeDirectory = Path.Combine(MinecraftDirectory, "runtime", componentName);
        if (!Directory.Exists(runtimeDirectory)) return false;

        var executable = OperatingSystem.IsWindows() ? "java.exe" : "java";
        try
        {
            return Directory.EnumerateFiles(runtimeDirectory, executable, SearchOption.AllDirectories).Any();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void UpdateJavaRuntimeInstalledStatus()
    {
        if (_allJavaRuntimes.Count == 0) return;

        foreach (var item in _allJavaRuntimes)
            item.IsInstalled = IsJavaRuntimeInstalled(item.ComponentName);

        UpdateJavaRuntimeCatalogStatus();
    }

    private void UpdateJavaRuntimeCatalogStatus()
    {
        if (_allJavaRuntimes.Count == 0)
        {
            if (!IsJavaRuntimeCatalogLoading) JavaRuntimeCatalogStatus = "尚未加载 Java 运行时列表";
            return;
        }

        var installedCount = _allJavaRuntimes.Count(item => item.IsInstalled);
        JavaRuntimeCatalogStatus = $"已安装 {installedCount} / {_allJavaRuntimes.Count}";
    }

    [RelayCommand]
    private async Task DownloadJavaAsync()
    {
        if (!CanDownloadJava || _resolved is null) return;

        await DownloadJavaRuntimeAsync(
            _resolved.Merged.JavaVersion?.Component,
            _resolved.RequiredJavaMajor,
            $"Java {_resolved.RequiredJavaMajor}");
    }

    private async Task DownloadJavaRuntimeAsync(string? component, int requiredMajor, string label)
    {
        if (IsDownloadingJava || requiredMajor <= 0) return;

        using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _javaDownloadCts = downloadCts;

        IsDownloadingJava = true;
        CurrentPage = AppPage.Download;
        EnterTaskSection();
        JavaDownloadPercent = 0;
        JavaDownloadText = "正在获取下载信息...";
        _javaCompletedFiles = 0;
        _javaTotalFiles = 0;
        _javaSpeedMeter.Reset();
        NotifyDownloadStats();
        Status = $"正在获取 {label} 下载信息...";
        LauncherLog.Info($"开始下载 {label}");

        try
        {
            var mirror = ResolveGameMirror();

            var progress = new Progress<JavaRuntimeDownloadProgress>(report =>
            {
                _javaCompletedFiles = report.CompletedFiles;
                _javaTotalFiles = report.TotalFiles;

                var percent = report.Percent is double value ? $"（{value:0}%）" : string.Empty;
                var megabytes = report.BytesDownloaded / 1024.0 / 1024.0;
                var speed = _javaSpeedMeter.Feed(report.BytesDownloaded);
                var speedText = speed.Length > 0 ? $"，{speed}" : string.Empty;
                JavaDownloadPercent = report.Percent ?? 0;
                JavaDownloadText = $"{report.CompletedFiles}/{report.TotalFiles} 个文件，{megabytes:0.0} MB{speedText}";
                Status = report.TotalFiles > 0
                    ? $"正在下载 {label}：{report.CompletedFiles}/{report.TotalFiles} 个文件，{megabytes:0.0} MB{speedText}{percent}"
                    : $"正在下载 {label}...";
                NotifyDownloadStats();
            });

            var result = await JavaRuntimeDownloader.DownloadAsync(
                MinecraftDirectory,
                component,
                requiredMajor,
                progress,
                new JavaRuntimeDownloadOptions
                {
                    Mirror = mirror,
                },
                downloadCts.Token);

            var runtime = await JavaLocator.ProbeAsync(
                result.JavaExecutablePath, MinecraftDirectory, _lifetime.Token);

            if (runtime is null)
            {
                Status = $"{label} 下载完成，但无法运行";
                JavaDownloadText = "下载完成，但无法运行";
                return;
            }

            var existing = JavaRuntimes.FirstOrDefault(item =>
                string.Equals(item.Path, runtime.Path, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
                JavaRuntimes.Add(runtime);
            else
                runtime = existing;

            SelectedJava = runtime;
            JavaDownloadPercent = 100;
            JavaDownloadText = $"已安装 {label}";
            Status = $"{label} 下载完成";
            LauncherLog.Info($"{label} 下载完成：{runtime.Path}");

            foreach (var item in _allJavaRuntimes)
            {
                if (string.Equals(item.ComponentName, result.ComponentName, StringComparison.OrdinalIgnoreCase))
                    item.IsInstalled = true;
            }
            UpdateJavaRuntimeCatalogStatus();
        }
        catch (OperationCanceledException)
        {
            Status = "Java 下载已取消";
            JavaDownloadText = "下载已取消";
            LauncherLog.Warn($"{label} 下载已取消");
        }
        catch (Exception exception)
        {
            Status = "Java 下载失败";
            JavaDownloadText = "下载失败";
            LauncherLog.Error($"{label} 下载失败", exception);
        }
        finally
        {
            IsDownloadingJava = false;
            if (ReferenceEquals(_javaDownloadCts, downloadCts)) _javaDownloadCts = null;
            UpdateJavaRuntimeInstalledStatus();
        }
    }

    [RelayCommand]
    private async Task LaunchAsync()
    {
        if (!CanLaunch || _resolved is null || SelectedJava is null) return;

        IsLaunching = true;
        Status = "正在启动...";

        try
        {
            var options = new LaunchOptions(PlayerName, MemoryMb, IsVersionIsolated);
            var plan = LaunchPlanBuilder.Build(_resolved, SelectedJava, options);

            LauncherLog.Info($"启动游戏：{_resolved.Id}（Java {SelectedJava.MajorVersion}）");

            // 游戏输出同时写入 NCL/log。
            var gameLog = new Progress<string>(line => LauncherLog.Game(line));

            var exitCode = await GameLauncher.RunAsync(
                plan,
                gameLog,
                _lifetime.Token);

            Status = exitCode == 0 ? "游戏已退出" : $"游戏异常退出（代码 {exitCode}）";
            LauncherLog.Info($"游戏退出，代码 {exitCode}");
        }
        catch (OperationCanceledException)
        {
            Status = "启动已取消";
            LauncherLog.Warn("启动已取消");
        }
        catch (Exception exception)
        {
            Status = "启动失败";
            LauncherLog.Error("启动失败", exception);
        }
        finally
        {
            IsLaunching = false;
        }
    }

    public async Task SetPreferredJavaAsync(string javaPath)
    {
        _settings.JavaPath = javaPath;
        _settings.Save();

        await RefreshJavaAsync();

        var selected = JavaRuntimes.FirstOrDefault(runtime =>
            string.Equals(runtime.Path, javaPath, StringComparison.OrdinalIgnoreCase));

        if (selected is not null) SelectedJava = selected;
    }

    private async Task RefreshJavaAsync()
    {
        var runtimes = await JavaLocator.DiscoverAsync(MinecraftDirectory, _settings.JavaPath, _lifetime.Token);

        JavaRuntimes.Clear();
        foreach (var runtime in runtimes)
            JavaRuntimes.Add(runtime);

        SelectedJava = JavaLocator.SelectBest(JavaRuntimes, _resolved?.RequiredJavaMajor ?? 8);
        UpdateLaunchWarning();
    }

    private async Task LoadDetailsAsync(VersionItemViewModel? item)
    {
        var revision = ++_detailsRevision;

        if (item is null)
        {
            _resolved = null;
            VersionDetails = "请选择左侧版本。";
            UpdateLaunchWarning();
            return;
        }

        VersionDetails = "正在解析版本...";
        _resolved = null;
        UpdateLaunchWarning();

        try
        {
            var resolved = await Task.Run(() => VersionResolver.Resolve(MinecraftDirectory, item.Id), _lifetime.Token);
            if (revision != _detailsRevision) return;

            _resolved = resolved;
            IsVersionIsolated = resolved.Isolation.IsIsolated;
            OnPropertyChanged(nameof(EffectiveGameDirectory));
            VersionDetails = BuildDetailsText(resolved);

            var bestJava = JavaLocator.SelectBest(JavaRuntimes, resolved.RequiredJavaMajor);
            if (bestJava is not null) SelectedJava = bestJava;

            UpdateLaunchWarning();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (revision != _detailsRevision) return;

            _resolved = null;
            VersionDetails = "解析失败: " + exception.Message;
            UpdateLaunchWarning();
        }
    }

    private static string BuildDetailsText(ResolvedVersion version)
    {
        var lines = new List<string>
        {
            $"版本: {version.Id}",
            $"类型: {version.Merged.Type ?? "未知"}",
            $"需要 Java: {version.RequiredJavaMajor}",
            $"依赖库: {version.Libraries.Count} 个",
            $"版本隔离: {version.Isolation.Description}",
            $"缺失文件: {version.MissingFiles.Count} 个",
        };

        if (version.MissingFiles.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("缺失示例:");
            foreach (var file in version.MissingFiles.Take(5))
                lines.Add("  " + file);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void UpdateLaunchWarning()
    {
        if (_resolved is null)
        {
            LaunchWarning = null;
            OnPropertyChanged(nameof(CanLaunch));
            OnPropertyChanged(nameof(CanDownloadJava));
            OnPropertyChanged(nameof(RequiredJavaDescription));
            return;
        }

        if (!_resolved.IsComplete)
        {
            LaunchWarning = $"缺少 {_resolved.MissingFiles.Count} 个必要文件（客户端 jar / 依赖库 / 资源索引），当前无法启动。";
        }
        else if (SelectedJava is null)
        {
            LaunchWarning = $"未找到可用 Java，需要 Java {_resolved.RequiredJavaMajor}+。";
        }
        else if (SelectedJava.MajorVersion < _resolved.RequiredJavaMajor)
        {
            LaunchWarning = $"当前 Java {SelectedJava.MajorVersion} 低于所需的 Java {_resolved.RequiredJavaMajor}。";
        }
        else
        {
            LaunchWarning = null;
        }

        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(CanDownloadJava));
        OnPropertyChanged(nameof(RequiredJavaDescription));
    }

    public void Dispose()
    {
        try
        {
            _loaderPrefetcher.LoaderVersionsCached -= OnLoaderVersionsCached;
            _loaderVersionsCts?.Cancel();
            _loaderVersionsCts?.Dispose();
            _lifetime.Cancel();
            _lifetime.Dispose();
        }
        catch (Exception)
        {
        }
    }
}
