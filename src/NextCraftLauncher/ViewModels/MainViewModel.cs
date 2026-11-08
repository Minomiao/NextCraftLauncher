using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
using NextCraftLauncher.Download.Mods;

namespace NextCraftLauncher.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _lifetime = new();

    private ResolvedVersion? _resolved;
    private int _detailsRevision;
    private Task _detailsTask = Task.CompletedTask;
    private CancellationTokenSource? _loaderVersionsCts;
    private int _loaderVersionsGeneration;
    private bool _loaderMirrorDirty;
    private string? _selectedLoaderKey;
    private string? _selectedLoaderVersion;
    private string? _selectedFabricApiVersion;
    private string? _fabricApiLoadedVersion;
    private CancellationTokenSource? _fabricApiCts;
    private bool _versionNameEdited;
    private bool _updatingVersionName;
    private bool _initialized;
    private int _activeTaskCount;
    private readonly LoaderVersionPrefetcher _loaderPrefetcher;

    public MainViewModel()
    {
        _settings = AppSettings.Load();
        _minecraftDirectory = _settings.MinecraftDirectory;
        ModCatalog.CurseForgeApiKey = _settings.CurseForgeApiKey;
        _playerName = _settings.PlayerName;
        _memoryMb = MemoryOptions.Contains(_settings.MemoryMb) ? _settings.MemoryMb : 4096;
        _selectedGameMirrorIndex = (int)_settings.GameMirror;
        _selectedForgeMirrorIndex = LoaderMirrorIndexFrom(_settings.ForgeMirror);
        _selectedFabricMirrorIndex = LoaderMirrorIndexFrom(_settings.FabricMirror);
        _selectedNeoForgeMirrorIndex = LoaderMirrorIndexFrom(_settings.NeoForgeMirror);
        _selectedIsolationIndex = (int)_settings.DefaultIsolation;
        _selectedLauncherVisibilityIndex = (int)_settings.LauncherVisibility;
        _selectedProcessPriorityIndex = (int)_settings.GameProcessPriority;
        _selectedWindowSizeIndex = (int)_settings.GameWindowSize;
        _customWindowWidth = _settings.GameWindowWidth;
        _customWindowHeight = _settings.GameWindowHeight;

        foreach (var directory in _settings.GameDirectories)
            GameFolders.Add(new GameFolderItemViewModel(directory));

        _selectedGameFolder = GameFolders.FirstOrDefault(folder =>
                                  string.Equals(folder.Path, _minecraftDirectory, StringComparison.OrdinalIgnoreCase))
                              ?? GameFolders.FirstOrDefault();

        LoaderGroups.Add(new LoaderVersionGroupViewModel("forge", "Forge"));
        LoaderGroups.Add(new LoaderVersionGroupViewModel("fabric", "Fabric"));
        LoaderGroups.Add(new LoaderVersionGroupViewModel("neoforge", "NeoForge"));

        // 后台慢慢补全各游戏版本的加载器列表，结果写入 NCL/cache/loaders。
        _loaderPrefetcher = new LoaderVersionPrefetcher(
            ResolvePrefetchMirror,
            isBusy: () => Volatile.Read(ref _activeTaskCount) > 0 || IsRemoteVersionsLoading);
        _loaderPrefetcher.LoaderVersionsCached += OnLoaderVersionsCached;

        // 搜索框延迟筛选，避免每敲一个字符都重建整张列表。
        _remoteSearchTimer.Tick += (_, _) =>
        {
            _remoteSearchTimer.Stop();
            ApplyRemoteVersionFilter();
        };
    }

    /// <summary>
    /// 启动界面期间完成本地初始化：扫描版本、发现 Java，然后让远程清单与后台预取自行运行。
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        // 主界面只等待本地版本扫描；Java 发现可能很慢，放到后台完成后再刷新界面。
        await RefreshCoreAsync(refreshJava: false);
        _ = RefreshJavaSafeAsync();

        // 先把上次缓存的远程清单填上，主界面打开时下载页已有内容；网络刷新在后台继续。
        var cached = await Task.Run(MinecraftVersionCatalog.TryLoadCached, _lifetime.Token);
        if (cached is not null && !_remoteManifestLoaded)
            ApplyRemoteManifest(cached, markLoaded: false);

        LauncherLog.Info("本地初始化完成");

        _ = PrefetchRemoteVersionsAsync();
        _ = Task.Run(RunLoaderPrefetcherAsync);

        // Fabric API 的版本表很小（约 48 KB），启动时后台拉一次；之后各游戏版本的列表都在本地过滤。
        var fabricMirror = ResolveLoaderMirror("fabric");
        _ = Task.Run(() => LoaderVersionCatalog.PrimeFabricApiAsync(fabricMirror, _lifetime.Token));
    }

    private async Task RefreshJavaSafeAsync()
    {
        try
        {
            await RefreshJavaAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            LauncherLog.Warn($"Java 发现失败：{exception.Message}");
        }
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
        // 后台预取会处理成百上千个版本，详情页没打开时不必往 UI 线程排队。
        if (!string.Equals(_activeLoaderVersionId, minecraftVersion, StringComparison.OrdinalIgnoreCase))
            return;

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
            var selectedVersion = string.Equals(group.Key, FabricApiGroup.Key, StringComparison.OrdinalIgnoreCase)
                ? _selectedFabricApiVersion
                : _selectedLoaderVersion;

            if (IsLoaderSelected(group.Key, selectedVersion) &&
                !group.Versions.Any(item =>
                    string.Equals(item.Version, selectedVersion, StringComparison.Ordinal)))
            {
                if (string.Equals(group.Key, FabricApiGroup.Key, StringComparison.OrdinalIgnoreCase))
                    ClearFabricApiSelection();
                else
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

    public RangeObservableCollection<RemoteVersionListRowViewModel> RemoteVersionRows { get; } = new();

    /// <summary>版本详情页里的加载器分组（Forge / Fabric / NeoForge）。</summary>
    public ObservableCollection<LoaderVersionGroupViewModel> LoaderGroups { get; } = new();

    /// <summary>选中 Fabric 后出现的可选分组：Fabric API，与加载器分组同级展示。</summary>
    public LoaderVersionGroupViewModel FabricApiGroup { get; } = new("fabric-api", "Fabric API");

    /// <summary>只有在选中了某个 Fabric 加载器版本后才显示 Fabric API 分组。</summary>
    public bool IsFabricApiGroupVisible =>
        string.Equals(_selectedLoaderKey, "fabric", StringComparison.OrdinalIgnoreCase) &&
        _selectedLoaderVersion is not null;

    /// <summary>版本详情页的扁平行列表，和游戏版本列表共用同一套行样式。</summary>
    public RangeObservableCollection<LoaderVersionListRowViewModel> LoaderVersionRows { get; } = new();

    public ObservableCollection<RemoteJavaRuntimeItemViewModel> AvailableJavaRuntimes { get; } = new();

    /// <summary>任务页里的下载/安装任务，每个任务一张卡片；可同时存在多个。</summary>
    public ObservableCollection<DownloadTaskViewModel> DownloadTasks { get; } = new();

    public IReadOnlyList<string> RemoteVersionTypes { get; } = new[] { "全部", "正式版", "快照", "远古" };

    private readonly List<RemoteVersionItemViewModel> _allRemoteVersions = new();
    private readonly List<RemoteJavaRuntimeItemViewModel> _allJavaRuntimes = new();
    private bool _remoteManifestLoaded;
    private int _gameMirrorGeneration;
    private bool _javaRuntimeCatalogLoaded;
    private int _filteredRemoteVersionCount;
    // 重复筛选（如搜索框打字）时复用行包装对象，避免每次重建上千个实例。
    private readonly Dictionary<RemoteVersionItemViewModel, RemoteVersionListRowViewModel> _remoteVersionRows = new();
    private volatile string? _activeLoaderVersionId;
    private readonly DispatcherTimer _remoteSearchTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(120),
    };

    public ObservableCollection<JavaRuntime> JavaRuntimes { get; } = new();

    public ObservableCollection<int> MemoryOptions { get; } = new(new[] { 1024, 2048, 4096, 6144, 8192, 12288, 16384 });

    public IReadOnlyList<string> IsolationOptions { get; } = new[]
    {
        "关闭",
        "隔离可安装 Mod 的版本",
        "隔离非正式版",
        "隔离可安装 Mod 的版本与非正式版",
        "隔离所有版本",
    };

    public IReadOnlyList<string> LauncherVisibilityOptions { get; } = new[]
    {
        "游戏启动后保持不变",
        "最小化",
        "隐藏（启动器退到后台）",
    };

    public IReadOnlyList<string> ProcessPriorityOptions { get; } = new[]
    {
        "低",
        "低于正常",
        "正常（默认）",
        "高于正常",
        "高",
    };

    public IReadOnlyList<string> WindowSizeOptions { get; } = new[]
    {
        "全屏",
        "默认",
        "与启动器尺寸一致",
        "自定义尺寸",
        "最大化",
    };

    public bool IsCustomWindowSize =>
        SelectedWindowSizeIndex == (int)GameWindowSizeMode.Custom;

    /// <summary>当前版本隔离方式的来源说明；未选版本时为空。</summary>
    public string IsolationDescription => _resolved?.Isolation.Description ?? string.Empty;

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
    [ObservableProperty] private string _remoteVersionSearch = string.Empty;
    [ObservableProperty] private string _remoteVersionType = "全部";
    [ObservableProperty] private string _remoteVersionsStatus = "尚未加载版本清单";
    [ObservableProperty] private bool _isRemoteVersionsLoading;
    [ObservableProperty] private RemoteVersionItemViewModel? _selectedRemoteVersion;
    [ObservableProperty] private bool _isRemoteVersionDetailOpen;

    // ---- 启动设置（与 PCL2 的「启动选项」对应） ----
    [ObservableProperty] private int _selectedIsolationIndex;
    [ObservableProperty] private int _selectedLauncherVisibilityIndex;
    [ObservableProperty] private int _selectedProcessPriorityIndex;
    [ObservableProperty] private int _selectedWindowSizeIndex;
    [ObservableProperty] private double _customWindowWidth = 1280;
    [ObservableProperty] private double _customWindowHeight = 720;
    [ObservableProperty] private string _versionName = string.Empty;
    [ObservableProperty] private bool _isJavaRuntimeCatalogLoading;
    [ObservableProperty] private string _javaRuntimeCatalogStatus = "尚未加载 Java 运行时列表";
    [ObservableProperty] private AppPage _currentPage = AppPage.Launch;
    [ObservableProperty] private int _selectedGameMirrorIndex;
    [ObservableProperty] private int _selectedForgeMirrorIndex;
    [ObservableProperty] private int _selectedFabricMirrorIndex;
    [ObservableProperty] private int _selectedNeoForgeMirrorIndex;
    [ObservableProperty] private LaunchSection _currentLaunchSection = LaunchSection.Start;
    [ObservableProperty] private DownloadSection _currentDownloadSection = DownloadSection.Versions;
    [ObservableProperty] private SettingsSection _currentSettingsSection = SettingsSection.Launch;
    [ObservableProperty] private bool _isVersionDeleteDialogOpen;
    [ObservableProperty] private string _versionDeleteDialogMessage = string.Empty;

    // 下载/安装失败时的置顶错误弹窗。
    [ObservableProperty] private bool _isDownloadErrorDialogOpen;
    [ObservableProperty] private string _downloadErrorTitle = string.Empty;
    [ObservableProperty] private string _downloadErrorHint = string.Empty;
    private Exception? _downloadErrorException;
    private string _downloadErrorContext = string.Empty;

    public bool HasDownloadErrorHint => !string.IsNullOrEmpty(DownloadErrorHint);

    partial void OnDownloadErrorHintChanged(string value)
        => OnPropertyChanged(nameof(HasDownloadErrorHint));

    [RelayCommand]
    private void CloseDownloadErrorDialog()
    {
        IsDownloadErrorDialogOpen = false;
        DownloadErrorTitle = string.Empty;
        DownloadErrorHint = string.Empty;
        _downloadErrorException = null;
        _downloadErrorContext = string.Empty;
    }

    /// <summary>展示下载/安装失败的置顶弹窗；错误原因同时写入日志。</summary>
    private void ShowDownloadError(string context, Exception exception)
    {
        _downloadErrorContext = context;
        _downloadErrorException = exception;
        DownloadErrorTitle = $"{context} 时出错";
        DownloadErrorHint = DescribeLikelyCause(exception);
        IsDownloadErrorDialogOpen = true;
    }

    /// <summary>生成给他人排查用的错误报告：操作、异常全文与最近的日志。</summary>
    public string BuildErrorReport()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Next Craft Launcher 错误报告");
        builder.AppendLine($"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"系统：{Environment.OSVersion}");
        builder.AppendLine($"运行时：.NET {Environment.Version}");
        builder.AppendLine($"操作：{_downloadErrorContext}");
        if (HasDownloadErrorHint)
            builder.AppendLine($"推测：{DownloadErrorHint}");

        builder.AppendLine();
        builder.AppendLine("错误详情：");
        builder.AppendLine(_downloadErrorException?.ToString() ?? "（无异常信息）");
        builder.AppendLine();
        builder.AppendLine("===== 最近的日志 =====");
        builder.AppendLine(ReadRecentLog(400));
        return builder.ToString();
    }

    private static string ReadRecentLog(int maxLines)
    {
        try
        {
            var path = LauncherLog.CurrentFilePath;
            if (!File.Exists(path)) return "（没有日志文件）";

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
                if (lines.Count > maxLines) lines.RemoveAt(0);
            }

            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception exception)
        {
            return $"（读取日志失败：{exception.Message}）";
        }
    }

    /// <summary>根据异常文本猜测可能的原因；无法判断时返回空字符串（界面不显示该行）。</summary>
    private static string DescribeLikelyCause(Exception exception)
    {
        var messages = new StringBuilder();
        for (var current = exception; current is not null; current = current.InnerException)
            messages.Append(current.Message).Append(' ');

        var text = messages.ToString();

        if (Contains("certificate") || Contains("SSL") || Contains("TLS") || Contains("证书"))
            return "TLS 证书校验失败：下载源节点证书异常，或网络中存在代理/劫持。可到「下载设置」切换游戏版本源后重试。";

        if (Contains("429") || Contains("Too Many Requests") || Contains("限流"))
            return "下载源正在限流：稍等几分钟再试，或切换下载源。";

        if (Contains("503") || Contains("502") || Contains("504") || Contains("Bad Gateway") || Contains("Service Unavailable"))
            return "下载源暂时不可用：可切换下载源后重试。";

        if (Contains("404") || Contains("403") || Contains("Not Found") || Contains("Forbidden"))
            return "下载源上找不到对应文件：建议把游戏版本源切到「官方」后重试。";

        if (Contains("timed out") || Contains("timeout") || Contains("超时") || Contains("停滞") ||
            Contains("拒绝连接") || Contains("actively refused") || Contains("无法连接") ||
            Contains("Name or service not known") || Contains("无法解析"))
            return "网络连接超时或不可达：检查网络/代理设置，或切换下载源后重试。";

        if (Contains("No space") || Contains("disk full") || Contains("空间不足"))
            return "磁盘空间不足：请清理游戏目录所在磁盘后重试。";

        if (Contains("UnauthorizedAccess") || Contains("拒绝访问") || Contains("being used") || Contains("被占用"))
            return "文件无法写入：游戏目录权限不足，或文件正被其他程序（包括正在运行的游戏）占用。";

        if (Contains("SHA-1") || Contains("校验失败"))
            return "文件校验失败：下载到的内容不完整，可能是下载源或网络问题，重试或切换下载源。";

        return string.Empty;

        bool Contains(string keyword) => text.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private DownloadSection _previousDownloadSection = DownloadSection.Versions;

    public bool IsLaunchPageSelected => CurrentPage == AppPage.Launch;

    public bool IsDownloadPageSelected => CurrentPage == AppPage.Download;

    public bool IsSettingsPageSelected => CurrentPage == AppPage.Settings;

    public bool IsStartSectionSelected => CurrentLaunchSection == LaunchSection.Start;

    public bool IsVersionsSectionSelected => CurrentLaunchSection == LaunchSection.Versions;

    public bool IsSettingsLaunchSelected => CurrentSettingsSection == SettingsSection.Launch;

    public bool IsSettingsDownloadSelected => CurrentSettingsSection == SettingsSection.Download;

    public bool IsDownloadVersionsSelected => CurrentDownloadSection == DownloadSection.Versions;

    public bool IsDownloadModsSelected => CurrentDownloadSection == DownloadSection.Mods;

    public bool IsDownloadJavaSelected => CurrentDownloadSection == DownloadSection.Java;

    public bool IsDownloadRunningSelected => CurrentDownloadSection == DownloadSection.Running;

    /// <summary>任务页当前有卡片可显示（含刚结束、等待自动移除的任务）。</summary>
    public bool HasDownloadTasks => DownloadTasks.Count > 0;

    public bool HasActiveDownloads => DownloadTasks.Any(task => task.IsRunning);

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
        _resolved is not null &&
        _resolved.RequiredJavaMajor > 0;

    public bool CanDownloadSelectedVersion =>
        !IsBusy &&
        SelectedRemoteVersion is not null;

    public bool CanDownloadSelected =>
        !IsBusy &&
        !IsLaunching &&
        SelectedRemoteVersion is not null &&
        VersionNaming.Normalize(VersionName).Length > 0;

    public string PrimaryDownloadText
    {
        get
        {
            if (_selectedLoaderKey is null || _selectedLoaderVersion is null)
                return "下载选中版本";

            var text = $"下载并安装 {GetLoaderDisplayName(_selectedLoaderKey)} {_selectedLoaderVersion}";
            return string.IsNullOrEmpty(_selectedFabricApiVersion)
                ? text
                : text + " + Fabric API";
        }
    }

    public bool CanManageSelectedVersion =>
        !IsBusy &&
        !IsLaunching &&
        SelectedVersion is not null;


    /// <summary>所有运行中任务的平均进度。</summary>
    public double OverallDownloadPercent
    {
        get
        {
            var running = DownloadTasks.Where(task => task.IsRunning).ToArray();
            return running.Length == 0 ? 0 : running.Average(task => task.Percent);
        }
    }

    public string OverallDownloadPercentText => $"{OverallDownloadPercent:0.0} %";

    /// <summary>所有运行中任务的合计速度。</summary>
    public string CurrentDownloadSpeedText =>
        $"{DownloadTasks.Where(task => task.IsRunning).Sum(task => task.SpeedMeter.MegabytesPerSecond):0.00} MB/s";

    public int RemainingFilesCount =>
        DownloadTasks.Where(task => task.IsRunning).Sum(task => task.RemainingFiles);

    private void NotifyDownloadStats()
    {
        OnPropertyChanged(nameof(OverallDownloadPercent));
        OnPropertyChanged(nameof(OverallDownloadPercentText));
        OnPropertyChanged(nameof(CurrentDownloadSpeedText));
        OnPropertyChanged(nameof(RemainingFilesCount));
    }

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
    }

    partial void OnCurrentSettingsSectionChanged(SettingsSection value)
    {
        OnPropertyChanged(nameof(IsSettingsLaunchSelected));
        OnPropertyChanged(nameof(IsSettingsDownloadSelected));
    }

    partial void OnCurrentDownloadSectionChanged(DownloadSection value)
    {
        OnPropertyChanged(nameof(IsDownloadVersionsSelected));
        OnPropertyChanged(nameof(IsDownloadModsSelected));
        OnPropertyChanged(nameof(IsDownloadJavaSelected));
        OnPropertyChanged(nameof(IsDownloadRunningSelected));

        if (value == DownloadSection.Mods && !_modPageInitialized)
        {
            // 第一次进 Mod 页时按当前版本设置筛选并直接列出结果（优先用缓存）。
            _modPageInitialized = true;
            UpdateModFiltersFromSelection();
            _ = ShowModsAsync();
        }

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

    // ---- Mod 下载 ----

    private CancellationTokenSource? _modSearchCts;
    private CancellationTokenSource? _modDetailCts;
    // 只在第一次进入 Mod 页时套用当前版本的筛选并自动搜索，之后保留用户的搜索状态。
    private bool _modPageInitialized;
    // 当前结果列表对应的搜索条件，翻页时沿用这一组，避免筛选未生效时错位。
    private (ModPlatform? Platform, string Query, string? GameVersion, string? Loader)? _modActiveQuery;

    public RangeObservableCollection<ModProjectItemViewModel> ModProjects { get; } = new();

    public ObservableCollection<ModVersionItemViewModel> ModVersions { get; } = new();

    /// <summary>搜索时可选的原版版本；在打开 Mod 页时用本地版本填充。</summary>
    public ObservableCollection<string> ModGameVersions { get; } = new();

    public IReadOnlyList<string> ModSources { get; } = new[] { "全部", "CurseForge", "Modrinth" };

    public IReadOnlyList<string> ModLoaderOptions { get; } = new[] { "全部", "Forge", "Fabric", "NeoForge", "Quilt" };

    [ObservableProperty] private string _modSearchText = string.Empty;
    [ObservableProperty] private int _selectedModSourceIndex;
    [ObservableProperty] private int _selectedModGameVersionIndex;
    [ObservableProperty] private int _selectedModLoaderIndex;
    [ObservableProperty] private bool _isModsLoading;
    [ObservableProperty] private int _modPage = 1;
    [ObservableProperty] private int _modTotalPages = 1;
    [ObservableProperty] private string _modStatus = "输入关键字搜索 Mod";
    [ObservableProperty] private bool _isModDetailOpen;
    [ObservableProperty] private ModProjectItemViewModel? _selectedModProject;
    [ObservableProperty] private string _modDetailTitle = string.Empty;
    [ObservableProperty] private string _modDetailDescription = string.Empty;
    [ObservableProperty] private string _modDetailStatus = string.Empty;

    /// <summary>由视图提供：当前版本不兼容时让用户选择保存位置（参数：默认文件名、默认目录）。</summary>
    public Func<string, string, Task<string?>>? ModSaveFilePicker { get; set; }

    /// <summary>翻页控件：上一页 / 页码/总页 / 下一页。</summary>
    public string ModPageText => $"{ModPage} / {ModTotalPages}";

    public bool ShowModPager => ModTotalPages > 1;

    public bool CanGoPreviousModPage => ModPage > 1;

    public bool CanGoNextModPage => ModPage < ModTotalPages;

    partial void OnModPageChanged(int value) => RaiseModPagerState();

    partial void OnModTotalPagesChanged(int value) => RaiseModPagerState();

    private void RaiseModPagerState()
    {
        OnPropertyChanged(nameof(ModPageText));
        OnPropertyChanged(nameof(ShowModPager));
        OnPropertyChanged(nameof(CanGoPreviousModPage));
        OnPropertyChanged(nameof(CanGoNextModPage));
    }

    private ModPlatform? SelectedModPlatform => SelectedModSourceIndex switch
    {
        1 => ModPlatform.CurseForge,
        2 => ModPlatform.Modrinth,
        _ => null,
    };

    private string? SelectedModGameVersionFilter =>
        SelectedModGameVersionIndex > 0 && SelectedModGameVersionIndex < ModGameVersions.Count
            ? ModGameVersions[SelectedModGameVersionIndex]
            : null;

    private string? SelectedModLoaderFilter =>
        SelectedModLoaderIndex > 0 && SelectedModLoaderIndex < ModLoaderOptions.Count
            ? ModLoaderOptions[SelectedModLoaderIndex]
            : null;

    /// <summary>用本地版本填充搜索筛选，并默认跟随当前选中的版本（与 PCL2 一致）。</summary>
    private void UpdateModFiltersFromSelection()
    {
        var versions = Versions
            .Select(version => version.Summary.ClientVersion)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        ModGameVersions.Clear();
        ModGameVersions.Add("全部");
        foreach (var value in versions)
            ModGameVersions.Add(value);

        var target = SelectedVersion?.Summary.ClientVersion;
        var index = 0;
        if (!string.IsNullOrWhiteSpace(target))
        {
            for (var i = 1; i < ModGameVersions.Count; i++)
            {
                if (!string.Equals(ModGameVersions[i], target, StringComparison.OrdinalIgnoreCase)) continue;
                index = i;
                break;
            }
        }

        SelectedModGameVersionIndex = index;
        SelectedModLoaderIndex = SelectedVersion?.Summary.Loader switch
        {
            "Forge" => 1,
            "Fabric" => 2,
            "NeoForge" => 3,
            "Quilt" => 4,
            _ => 0,
        };
    }

    [RelayCommand]
    private Task SearchModsAsync() => FetchModPageAsync(1, newSearch: true, ignoreCache: true);

    /// <summary>进入 Mod 页时先用缓存展示，避免每次都等一次网络。</summary>
    private Task ShowModsAsync() => FetchModPageAsync(1, newSearch: true, ignoreCache: false);

    [RelayCommand]
    private Task GoPreviousModPageAsync() => FetchModPageAsync(Math.Max(1, ModPage - 1), newSearch: false);

    [RelayCommand]
    private Task GoNextModPageAsync() => FetchModPageAsync(Math.Min(ModTotalPages, ModPage + 1), newSearch: false);

    /// <summary>
    /// 取搜索结果的一页。<paramref name="newSearch"/> 为 true 时按当前筛选重新搜索，
    /// 否则翻页；<paramref name="ignoreCache"/> 为 true 时跳过本地缓存。
    /// </summary>
    private async Task FetchModPageAsync(int page, bool newSearch, bool ignoreCache = false)
    {
        _modSearchCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _modSearchCts = cts;

        if (newSearch)
        {
            _modActiveQuery = (
                SelectedModPlatform,
                ModSearchText.Trim(),
                SelectedModGameVersionFilter,
                SelectedModLoaderFilter);

            ModStatus = "正在搜索…";
            ModProjects.Clear();
            IsModDetailOpen = false;
        }
        else
        {
            if (_modActiveQuery is null) return;
            ModStatus = $"正在加载第 {page} 页…";
        }

        var request = _modActiveQuery!.Value;
        IsModsLoading = true;

        try
        {
            var result = await Task.Run(
                () => ModCatalog.SearchAsync(
                    request.Platform,
                    request.Query,
                    request.GameVersion,
                    request.Loader,
                    page,
                    ignoreCache,
                    cts.Token),
                cts.Token);

            if (cts.IsCancellationRequested) return;

            ModPage = result.Page;
            ModTotalPages = result.TotalPages;

            ModProjects.ReplaceAll(
                result.Projects.Select(project => new ModProjectItemViewModel(project, SelectModProject)));

            ModStatus = ModProjects.Count == 0 ? "没有找到 Mod" : string.Empty;

            LauncherLog.Info(
                $"Mod 搜索：\"{request.Query}\"（{request.GameVersion ?? "全部"} / " +
                $"{request.Loader ?? "全部"}）第 {result.Page}/{result.TotalPages} 页，" +
                $"共 {result.Total} 个结果，本页 {ModProjects.Count} 个");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ModStatus = "搜索失败：" + exception.Message;
            LauncherLog.Warn($"Mod 搜索失败：{exception.Message}");
        }
        finally
        {
            if (ReferenceEquals(_modSearchCts, cts)) IsModsLoading = false;
        }
    }

    private void SelectModProject(ModProjectItemViewModel item)
    {
        SelectedModProject = item;
        IsModDetailOpen = true;
        ModDetailTitle = item.Name;
        ModDetailDescription = item.Description;
        ModVersions.Clear();
        _ = LoadModVersionsAsync(item);
    }

    [RelayCommand]
    private void CloseModDetail()
    {
        _modDetailCts?.Cancel();
        IsModDetailOpen = false;
        SelectedModProject = null;
    }

    private async Task LoadModVersionsAsync(ModProjectItemViewModel item)
    {
        _modDetailCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _modDetailCts = cts;

        ModDetailStatus = "正在获取版本列表…";

        try
        {
            var versions = await Task.Run(
                () => ModCatalog.GetVersionsAsync(item.Project, null, null, cts.Token),
                cts.Token);

            if (cts.IsCancellationRequested) return;

            var ordered = versions
                .OrderByDescending(version => version.Published ?? DateTimeOffset.MinValue)
                .ToList();

            var items = new List<ModVersionItemViewModel>(ordered.Count);
            foreach (var version in ordered)
            {
                var vm = new ModVersionItemViewModel(version, item => _ = DownloadModVersionAsync(item));
                if (version.Dependencies.Count > 0)
                    vm.DependencyText = $"{version.Dependencies.Count} 项前置";
                ModVersions.Add(vm);
                items.Add(vm);
            }

            ModDetailStatus = items.Count == 0
                ? "该 Mod 没有可用文件"
                : $"共 {items.Count} 个文件";

            _ = FillDependencyTextAsync(item.Project.Platform, items, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ModDetailStatus = "获取版本失败：" + exception.Message;
        }
    }

    /// <summary>把前置 Mod 的 ID 换成名字，仅用于展示（PCL2 也不会自动下载单个 Mod 的前置）。</summary>
    private static async Task FillDependencyTextAsync(
        ModPlatform platform,
        IReadOnlyList<ModVersionItemViewModel> items,
        CancellationToken cancellationToken)
    {
        try
        {
            var ids = items.SelectMany(item => item.Version.Dependencies).Distinct(StringComparer.Ordinal).ToList();
            if (ids.Count == 0) return;

            var projects = await ModCatalog.GetProjectsAsync(platform, ids, cancellationToken);
            var names = projects.ToDictionary(project => project.Id, project => project.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var item in items)
            {
                if (item.Version.Dependencies.Count == 0) continue;

                item.DependencyText = "前置：" + string.Join("、", item.Version.Dependencies
                    .Select(id => names.TryGetValue(id, out var name) ? name : id));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            LauncherLog.Warn($"获取前置 Mod 名称失败：{exception.Message}");
        }
    }

    private async Task DownloadModVersionAsync(ModVersionItemViewModel item)
    {
        var version = item.Version;
        var target = ResolveModTargetPath(version);

        if (target is null)
        {
            // 当前没有兼容的版本：让用户自己选位置（等价于 PCL2 的「另存为」）。
            if (ModSaveFilePicker is null)
            {
                ModDetailStatus = "当前版本不兼容，请先选择一个兼容的游戏版本";
                return;
            }

            var defaultDirectory = Path.Combine(MinecraftDirectory, "mods");
            target = await ModSaveFilePicker(version.FileName, defaultDirectory);
            if (string.IsNullOrWhiteSpace(target)) return;
        }

        var task = TryCreateTask($"Mod · {item.Title}", DownloadTaskKind.Generic, $"mod:{version.Platform}:{version.Id}");
        if (task is null) return;

        task.StatusText = "正在下载…";
        Status = $"正在下载 Mod：{item.Title}";
        LauncherLog.Info($"下载 Mod {item.Title} → {target}");

        try
        {
            var progress = new Progress<DownloadProgress>(report =>
            {
                if (report.Percent is double percent && percent > task.Percent)
                    task.Percent = Math.Min(percent, 99);

                if (report.BytesReceived > 0)
                    task.SpeedMeter.Feed(report.BytesReceived);

                task.StatusText = $"{report.BytesReceived / 1048576.0:0.0} MB";
                NotifyDownloadStats();
            });

            await Task.Run(
                () => DownloadClient.Shared.DownloadFileAsync(
                    version.DownloadUrls,
                    target,
                    version.Sha1,
                    version.Size > 0 ? version.Size : null,
                    progress,
                    task.Token),
                task.Token);

            task.Percent = 100;
            var fileName = Path.GetFileName(target);
            FinishTask(task, DownloadTaskState.Completed, $"已下载到 {Path.GetDirectoryName(target)}");
            ModDetailStatus = $"已下载：{fileName}";
            Status = $"Mod 已下载：{fileName}";
        }
        catch (OperationCanceledException)
        {
            FinishTask(task, DownloadTaskState.Canceled, "下载已取消");
        }
        catch (Exception exception)
        {
            LauncherLog.Error($"下载 Mod 失败：{version.FileName}", exception);
            ShowDownloadError("下载 Mod", exception);
            FinishTask(task, DownloadTaskState.Failed, "下载失败");
        }
    }

    /// <summary>
    /// 计算下载目标：当前选中版本兼容时直接放进它的 mods 目录（已隔离则放版本目录），
    /// 否则返回 null，由调用方让用户选择位置。兼容性判断与 PCL2 一致：
    /// 游戏版本匹配，且 Mod 支持当前加载器。
    /// </summary>
    private string? ResolveModTargetPath(ModVersion version)
    {
        if (SelectedVersion is not { } selected) return null;

        var gameVersion = selected.Summary.ClientVersion;
        if (!string.IsNullOrWhiteSpace(gameVersion) && version.GameVersions.Count > 0 &&
            !version.GameVersions.Any(value => string.Equals(value, gameVersion, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var loaderTags = ModCatalog.LoaderTags(selected.Summary.Loader);
        if (version.Loaders.Count > 0 &&
            !version.Loaders.Any(loader => loaderTags.Contains(loader, StringComparer.OrdinalIgnoreCase)))
        {
            return null;
        }

        var gameDirectory = IsVersionIsolated && _resolved is not null
            ? _resolved.VersionDirectory
            : MinecraftDirectory;

        var modsDirectory = Path.Combine(gameDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);

        return Path.Combine(modsDirectory, SanitizeModFileName(version.FileName));
    }

    private static string SanitizeModFileName(string fileName)
    {
        var name = fileName;
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '-');

        // PCL2 的做法：~ 会导致 Mixin 加载失败。
        return name.Replace('~', '-');
    }

    // ---- 下载任务卡片 ----

    /// <summary>新建一个任务卡片并切到任务页；同一个去重键的任务已在运行时不重复创建。</summary>
    private DownloadTaskViewModel? TryCreateTask(string title, DownloadTaskKind kind, string key)
    {
        var duplicate = DownloadTasks.Any(task =>
            task.IsRunning &&
            task.Kind == kind &&
            string.Equals(task.Key, key, StringComparison.OrdinalIgnoreCase));

        if (duplicate)
        {
            Status = $"{title} 已在下载任务中";
            return null;
        }

        var task = new DownloadTaskViewModel(title, key, kind, _lifetime.Token);
        DownloadTasks.Add(task);
        OnPropertyChanged(nameof(HasDownloadTasks));
        RaiseActiveDownloadState();
        return task;
    }

    /// <summary>结束任务：写回终态，并在几秒后自动移除卡片。</summary>
    private void FinishTask(DownloadTaskViewModel task, DownloadTaskState state, string statusText)
    {
        task.StatusText = statusText;
        task.State = state;
        RaiseActiveDownloadState();

        try
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                RemoveTask(task);
            };
            timer.Start();
        }
        catch (Exception)
        {
            // 启动器正在关闭时调度器已停止，卡片留着也无影响。
        }
    }

    private void RemoveTask(DownloadTaskViewModel task)
    {
        if (!DownloadTasks.Remove(task)) return;

        OnPropertyChanged(nameof(HasDownloadTasks));
        RaiseActiveDownloadState();
        task.Dispose();
    }

    /// <summary>任务增减会影响左侧的聚合统计与入口按钮。</summary>
    private void RaiseActiveDownloadState()
    {
        // 后台预取会从另一个线程询问「是否忙」，不能直接读 ObservableCollection。
        Volatile.Write(ref _activeTaskCount, DownloadTasks.Count(task => task.IsRunning));
        OnPropertyChanged(nameof(HasActiveDownloads));
        NotifyDownloadStats();
    }

    /// <summary>把版本下载阶段映射到卡片上的三行步骤。</summary>
    private static void ApplyVersionStage(DownloadTaskViewModel task, MinecraftDownloadStage stage)
    {
        if (task.Steps.Count < 3) return;

        var (first, second, third) = (task.Steps[0], task.Steps[1], task.Steps[2]);

        switch (stage)
        {
            case MinecraftDownloadStage.Preparing:
                first.SetState(DownloadStepState.Active, "…");
                second.SetState(DownloadStepState.Pending);
                third.SetState(DownloadStepState.Pending);
                break;

            case MinecraftDownloadStage.Files:
                first.SetState(DownloadStepState.Done);
                if (second.State != DownloadStepState.Active)
                    second.SetState(DownloadStepState.Active, "…");
                third.SetState(DownloadStepState.Pending);
                break;

            case MinecraftDownloadStage.Finalizing:
                first.SetState(DownloadStepState.Done);
                second.SetState(DownloadStepState.Done);
                third.SetState(DownloadStepState.Active, "…");
                break;
        }
    }

    /// <summary>给版本任务卡片添上三步流程。</summary>
    private static void AddVersionSteps(DownloadTaskViewModel task)
    {
        task.AddStep("版本信息");
        task.AddStep("游戏文件");
        task.AddStep("写入配置");
        ApplyVersionStage(task, MinecraftDownloadStage.Preparing);
    }

    partial void OnCurrentPageChanged(AppPage value)
    {
        OnPropertyChanged(nameof(IsLaunchPageSelected));
        OnPropertyChanged(nameof(IsDownloadPageSelected));
        OnPropertyChanged(nameof(IsSettingsPageSelected));

        if (value == AppPage.Download)
        {
            var clock = Stopwatch.StartNew();

            if (HasActiveDownloads) EnterTaskSection(leaveVersionDetail: true);
            _ = EnsureRemoteVersionsLoadedAsync();
            _ = EnsureJavaRuntimesLoadedAsync();

            // 预取在启动时完成；若此处仍未完成，界面会显示“正在获取版本清单”。
            if (_remoteManifestLoaded && _filteredRemoteVersionCount == 0 && _allRemoteVersions.Count > 0)
                ApplyRemoteVersionFilter();

            // 首次打开要完成页面模板解析与布局；只在真的卡了的时候记一笔，
            // 平时（几毫秒）不刷日志。Background 低于 Render，能覆盖到渲染。
            Dispatcher.UIThread.Post(
                () =>
                {
                    var elapsed = clock.ElapsedMilliseconds;
                    if (elapsed >= 50)
                        LauncherLog.Info($"[PERF] 下载页首帧：{elapsed} ms");
                },
                DispatcherPriority.Background);
        }
    }

    partial void OnRemoteVersionSearchChanged(string value)
    {
        _remoteSearchTimer.Stop();
        _remoteSearchTimer.Start();
    }

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



    [RelayCommand]
    private void ShowLaunchPage() => CurrentPage = AppPage.Launch;

    [RelayCommand]
    private void ShowStartSection() => CurrentLaunchSection = LaunchSection.Start;

    [RelayCommand]
    private void ShowVersionsSection() => CurrentLaunchSection = LaunchSection.Versions;

    [RelayCommand]
    private void ShowDownloadVersions() => CurrentDownloadSection = DownloadSection.Versions;

    [RelayCommand]
    private void ShowDownloadMods() => CurrentDownloadSection = DownloadSection.Mods;

    [RelayCommand]
    private void ShowDownloadJava() => CurrentDownloadSection = DownloadSection.Java;

    [RelayCommand]
    private void ShowDownloadRunning() => EnterTaskSection();

    [RelayCommand]
    private void ReturnFromDownloadTasks() => CurrentDownloadSection = _previousDownloadSection;

    private void EnterTaskSection(bool leaveVersionDetail = false)
    {
        // 从下载页跳到任务页时，把版本详情收起来，返回后回到版本列表而不是停在详情页。
        if (leaveVersionDetail && IsRemoteVersionDetailOpen)
            CloseRemoteVersionDetail();

        if (CurrentDownloadSection != DownloadSection.Running)
            _previousDownloadSection = CurrentDownloadSection;

        CurrentDownloadSection = DownloadSection.Running;
    }


    [RelayCommand]
    private void ShowDownloadPage() => CurrentPage = AppPage.Download;

    [RelayCommand]
    private void ShowSettingsPage() => CurrentPage = AppPage.Settings;

    [RelayCommand]
    private void ShowSettingsLaunch() => CurrentSettingsSection = SettingsSection.Launch;

    [RelayCommand]
    private void ShowSettingsDownload() => CurrentSettingsSection = SettingsSection.Download;

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

    partial void OnSelectedIsolationIndexChanged(int value)
    {
        var mode = value is >= 0 and <= 4 ? (DefaultIsolationMode)value : DefaultIsolationMode.All;
        if (_settings.DefaultIsolation == mode) return;

        _settings.DefaultIsolation = mode;
        _settings.Save();

        // 重新解析当前版本，让隔离方式立即按新默认值生效。
        if (SelectedVersion is not null)
            _ = LoadDetailsAsync(SelectedVersion);
    }

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

    partial void OnSelectedLauncherVisibilityIndexChanged(int value)
    {
        var visibility = value is >= 0 and <= 2
            ? (LauncherVisibilityAfterLaunch)value
            : LauncherVisibilityAfterLaunch.Keep;
        if (_settings.LauncherVisibility == visibility) return;

        _settings.LauncherVisibility = visibility;
        _settings.Save();
    }

    partial void OnSelectedProcessPriorityIndexChanged(int value)
    {
        var priority = value is >= 0 and <= 4 ? (GameProcessPriority)value : GameProcessPriority.Normal;
        if (_settings.GameProcessPriority == priority) return;

        _settings.GameProcessPriority = priority;
        _settings.Save();
    }

    partial void OnSelectedWindowSizeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsCustomWindowSize));

        var size = value is >= 0 and <= 4 ? (GameWindowSizeMode)value : GameWindowSizeMode.Default;
        if (_settings.GameWindowSize == size) return;

        _settings.GameWindowSize = size;
        _settings.Save();
    }

    partial void OnCustomWindowWidthChanged(double value)
    {
        var width = (int)Math.Clamp(value, 320, 7680);
        if (_settings.GameWindowWidth == width) return;

        _settings.GameWindowWidth = width;
        _settings.Save();
    }

    partial void OnCustomWindowHeightChanged(double value)
    {
        var height = (int)Math.Clamp(value, 240, 4320);
        if (_settings.GameWindowHeight == height) return;

        _settings.GameWindowHeight = height;
        _settings.Save();
    }

    private DownloadMirror ResolveGameMirror() => ToDownloadMirror(SelectedGameMirrorIndex);

    private DownloadMirror ResolveLoaderMirror(string loader) => loader.ToLowerInvariant() switch
    {
        "forge" => LoaderMirrorFromIndex(SelectedForgeMirrorIndex),
        "fabric" or "fabric-api" => LoaderMirrorFromIndex(SelectedFabricMirrorIndex),
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
    private Task RefreshAsync() => RefreshCoreAsync(refreshJava: true);

    private async Task RefreshCoreAsync(bool refreshJava)
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

            if (refreshJava)
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
            SelectedIsolationIndex = isolated ? (int)DefaultIsolationMode.All : (int)DefaultIsolationMode.Off;
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
        if (markLoaded) _remoteManifestLoaded = true;

        // 启动时会先用缓存、再用网络结果各刷新一次。内容没变就不要重建集合：
        // 重建会让列表整体 Reset，正在按下的行被替换，点击会被丢掉。
        if (SameManifest(result.Versions))
        {
            UpdateRemoteInstalledStatus();
            return;
        }

        var clock = Stopwatch.StartNew();
        var selectedId = SelectedRemoteVersion?.Id;

        _allRemoteVersions.Clear();
        _remoteVersionRows.Clear();
        foreach (var entry in result.Versions)
            _allRemoteVersions.Add(new RemoteVersionItemViewModel(entry, SelectRemoteVersion));

        // 重新构建集合后把选中的版本重新指向新实例，避免详情页引用旧对象。
        if (selectedId is not null)
        {
            var selected = _allRemoteVersions.FirstOrDefault(item =>
                string.Equals(item.Id, selectedId, StringComparison.OrdinalIgnoreCase));

            if (selected is not null)
                SelectedRemoteVersion = selected;
            else if (IsRemoteVersionDetailOpen)
            {
                _activeLoaderVersionId = null;
                IsRemoteVersionDetailOpen = false;
            }
        }

        UpdateRemoteInstalledStatus();
        ApplyRemoteVersionFilter();
        LauncherLog.Info($"版本清单已重建：{_allRemoteVersions.Count} 个版本（{clock.ElapsedMilliseconds} ms）");
    }

    /// <summary>比较版本列表是否与当前一致（不看 URL / 哈希，这些不影响列表展示）。</summary>
    private bool SameManifest(IReadOnlyList<MinecraftVersionEntry> versions)
    {
        if (versions.Count != _allRemoteVersions.Count) return false;

        for (var index = 0; index < versions.Count; index++)
        {
            var incoming = versions[index];
            var current = _allRemoteVersions[index];

            if (!string.Equals(incoming.Id, current.Id, StringComparison.Ordinal) ||
                !string.Equals(incoming.Type ?? "unknown", current.Type, StringComparison.Ordinal) ||
                incoming.ReleaseTime != current.ReleaseTime)
            {
                return false;
            }
        }

        return true;
    }

    private async Task EnsureRemoteVersionsLoadedAsync()
    {
        if (_remoteManifestLoaded || IsRemoteVersionsLoading) return;
        await LoadRemoteVersionsAsync();
    }

    private void ApplyRemoteVersionFilter()
    {
        var rows = new List<RemoteVersionListRowViewModel>();

        var search = RemoteVersionSearch.Trim();
        var filtered = new List<RemoteVersionItemViewModel>();

        foreach (var item in _allRemoteVersions)
        {
            if (!MatchesRemoteVersionType(item)) continue;
            if (search.Length > 0 && !item.Id.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            filtered.Add(item);
        }

        _filteredRemoteVersionCount = filtered.Count;

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
                rows.Add(GetRemoteVersionRow(item));
            }
        }

        RemoteVersionRows.ReplaceAll(rows);
        OnPropertyChanged(nameof(ShowRemoteVersionsLoading));
        UpdateRemoteVersionsStatus();
    }

    private RemoteVersionListRowViewModel GetRemoteVersionRow(RemoteVersionItemViewModel item)
    {
        if (_remoteVersionRows.TryGetValue(item, out var row)) return row;

        row = RemoteVersionListRowViewModel.CreateVersion(item);
        _remoteVersionRows[item] = row;
        return row;
    }

    private void SelectRemoteVersion(RemoteVersionItemViewModel item)
    {
        SelectedRemoteVersion = item;
        IsRemoteVersionDetailOpen = true;
        _activeLoaderVersionId = item.Id;

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
        _fabricApiCts?.Cancel();
        _activeLoaderVersionId = null;
        IsRemoteVersionDetailOpen = false;
    }

    private void ResetLoaderGroups()
    {
        ClearLoaderSelection();

        _fabricApiCts?.Cancel();
        _fabricApiLoadedVersion = null;
        _selectedFabricApiVersion = null;
        FabricApiGroup.SelectedVersionText = null;
        FabricApiGroup.Versions.Clear();
        FabricApiGroup.IsAvailable = false;
        FabricApiGroup.IsExpanded = false;
        FabricApiGroup.IsLoading = false;
        FabricApiGroup.ErrorText = null;
        FabricApiGroup.StatusText = "加载中…";

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
            AppendGroupRows(rows, group);

            // Fabric API 与加载器分组同级，紧跟在 Fabric 分组后面。
            if (group.Key == "fabric" && IsFabricApiGroupVisible)
                AppendGroupRows(rows, FabricApiGroup);
        }

        LoaderVersionRows.ReplaceAll(rows);
    }

    private void AppendGroupRows(List<LoaderVersionListRowViewModel> rows, LoaderVersionGroupViewModel group)
    {
        rows.Add(CreateLoaderHeaderRow(group));

        if (!group.IsExpanded) return;

        if (group.Versions.Count == 0)
        {
            rows.Add(LoaderVersionListRowViewModel.CreateEmpty(group, DescribeLoaderEmpty(group)));
            return;
        }

        foreach (var version in group.Versions)
            rows.Add(LoaderVersionListRowViewModel.CreateVersion(version));
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
    {
        if (version is null) return false;

        if (string.Equals(loaderKey, FabricApiGroup.Key, StringComparison.OrdinalIgnoreCase))
            return string.Equals(_selectedFabricApiVersion, version, StringComparison.Ordinal);

        return loaderKey is not null &&
               string.Equals(_selectedLoaderKey, loaderKey, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(_selectedLoaderVersion, version, StringComparison.Ordinal);
    }

    private void SelectLoaderVersion(LoaderVersionItemViewModel item)
    {
        // Fabric API 的选择独立于加载器单选。
        if (string.Equals(item.LoaderKey, FabricApiGroup.Key, StringComparison.OrdinalIgnoreCase))
        {
            SelectFabricApiVersion(item);
            return;
        }

        // 加载器互不兼容：再次点击已选中的行即取消，只能保留一个选择。
        if (item.IsSelected)
        {
            ClearLoaderSelection();
            return;
        }

        var wasFabricApiVisible = IsFabricApiGroupVisible;

        _selectedLoaderKey = item.LoaderKey;
        _selectedLoaderVersion = item.Version;

        foreach (var group in LoaderGroups)
        {
            group.SelectedVersionText = string.Equals(group.Key, item.LoaderKey, StringComparison.OrdinalIgnoreCase)
                ? item.Version
                : null;

            foreach (var version in group.Versions)
                version.IsSelected = ReferenceEquals(version, item);
        }

        NotifyLoaderSelectionChanged();
        RefreshDefaultVersionName();

        if (IsFabricApiGroupVisible != wasFabricApiVisible)
            OnFabricApiVisibilityChanged(SelectedRemoteVersion?.Id);

        // 选完就收起该分组，标题旁已经能看到选中的版本。
        CollapseGroup(item.LoaderKey);
        RefreshLoaderGroupHeaders();
    }

    private void ClearLoaderSelection()
    {
        var wasFabricApiVisible = IsFabricApiGroupVisible;

        _selectedLoaderKey = null;
        _selectedLoaderVersion = null;

        foreach (var group in LoaderGroups)
        {
            group.SelectedVersionText = null;

            foreach (var version in group.Versions)
                version.IsSelected = false;
        }

        NotifyLoaderSelectionChanged();
        RefreshDefaultVersionName();

        if (wasFabricApiVisible)
            OnFabricApiVisibilityChanged(SelectedRemoteVersion?.Id);
        else
            RefreshLoaderGroupHeaders();
    }

    /// <summary>重画所有分组的标题行（已选版本可能已变化）。</summary>
    private void RefreshLoaderGroupHeaders()
    {
        foreach (var group in LoaderGroups)
            UpdateLoaderGroupRows(group, contentChanged: false);

        if (IsFabricApiGroupVisible)
            UpdateLoaderGroupRows(FabricApiGroup, contentChanged: false);
    }

    /// <summary>收起某个分组；已收起时只刷新标题以显示新的选中版本。</summary>
    private void CollapseGroup(string loaderKey)
    {
        var group = string.Equals(loaderKey, FabricApiGroup.Key, StringComparison.OrdinalIgnoreCase)
            ? FabricApiGroup
            : LoaderGroups.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, loaderKey, StringComparison.OrdinalIgnoreCase));

        if (group is null) return;

        if (group.IsExpanded)
        {
            group.IsExpanded = false;
            UpdateLoaderGroupRows(group);
            return;
        }

        UpdateLoaderGroupRows(group, contentChanged: false);
    }

    /// <summary>Fabric API 分组的显示状态发生变化：出现时加载列表，隐藏时清除选择。</summary>
    private void OnFabricApiVisibilityChanged(string? minecraftVersion)
    {
        OnPropertyChanged(nameof(IsFabricApiGroupVisible));

        if (IsFabricApiGroupVisible && !string.IsNullOrEmpty(minecraftVersion))
            PrepareFabricApiGroup(minecraftVersion);
        else
            ClearFabricApiSelection();

        RebuildLoaderVersionRows();
    }

    /// <summary>展开 Fabric API 分组；同一游戏版本只请求一次。</summary>
    private void PrepareFabricApiGroup(string minecraftVersion)
    {
        FabricApiGroup.IsExpanded = true;

        if (string.Equals(_fabricApiLoadedVersion, minecraftVersion, StringComparison.OrdinalIgnoreCase))
            return;

        _fabricApiLoadedVersion = minecraftVersion;
        _selectedFabricApiVersion = null;
        FabricApiGroup.Versions.Clear();
        FabricApiGroup.IsAvailable = false;
        FabricApiGroup.IsLoading = true;
        FabricApiGroup.ErrorText = null;
        FabricApiGroup.StatusText = "加载中…";

        _fabricApiCts?.Cancel();
        _fabricApiCts?.Dispose();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _fabricApiCts = cts;

        _ = LoadFabricApiGroupSafeAsync(minecraftVersion, cts.Token);
    }

    private async Task LoadFabricApiGroupSafeAsync(string minecraftVersion, CancellationToken cancellationToken)
    {
        try
        {
            await LoadLoaderGroupAsync(
                FabricApiGroup, minecraftVersion, _loaderVersionsGeneration, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            FabricApiGroup.IsLoading = false;
            FabricApiGroup.ErrorText = exception.Message;
            FabricApiGroup.StatusText = "获取失败";
            UpdateLoaderGroupRows(FabricApiGroup);
        }
    }

    private void SelectFabricApiVersion(LoaderVersionItemViewModel item)
    {
        if (item.IsSelected)
        {
            _selectedFabricApiVersion = null;
            FabricApiGroup.SelectedVersionText = null;

            foreach (var version in FabricApiGroup.Versions)
                version.IsSelected = false;
        }
        else
        {
            _selectedFabricApiVersion = item.Version;
            FabricApiGroup.SelectedVersionText = item.Version;

            foreach (var version in FabricApiGroup.Versions)
                version.IsSelected = ReferenceEquals(version, item);
        }

        NotifyFabricApiSelectionChanged();
        CollapseGroup(FabricApiGroup.Key);
    }

    private void ClearFabricApiSelection()
    {
        _selectedFabricApiVersion = null;
        FabricApiGroup.SelectedVersionText = null;

        foreach (var version in FabricApiGroup.Versions)
            version.IsSelected = false;

        NotifyFabricApiSelectionChanged();
    }

    private void NotifyFabricApiSelectionChanged()
    {
        OnPropertyChanged(nameof(PrimaryDownloadText));
        OnPropertyChanged(nameof(CanDownloadSelected));
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

    /// <summary>读取加载器版本缓存；文件读取与反序列化都在线程池完成。</summary>
    private static Task<(LoaderVersionResult Result, DateTimeOffset UpdatedAt)?> LoadCachedLoaderVersionsAsync(
        string loader,
        string minecraftVersion,
        CancellationToken cancellationToken)
        => Task.Run<(LoaderVersionResult, DateTimeOffset)?>(() =>
        {
            if (!LoaderVersionStore.TryLoad(loader, minecraftVersion, out var result, out var updatedAt))
                return null;

            return (result, updatedAt);
        }, cancellationToken);

    private async Task LoadLoaderGroupAsync(
        LoaderVersionGroupViewModel group,
        string minecraftVersion,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            // 1) 先用本地缓存（72 小时内）秒开，不等网络。
            //    文件读取与 JSON 解析放线程池，避免点开详情页时卡住界面。
            var cached = await LoadCachedLoaderVersionsAsync(group.Key, minecraftVersion, cancellationToken);
            if (cached is { } cache && LoaderVersionStore.IsFresh(cache.UpdatedAt))
            {
                ApplyLoaderGroupResult(group, cache.Result);
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
        RemoteVersionsStatus = _filteredRemoteVersionCount == _allRemoteVersions.Count
            ? $"共 {_allRemoteVersions.Count} 个版本 · 已安装 {installedCount} 个"
            : $"显示 {_filteredRemoteVersionCount} / {_allRemoteVersions.Count} 个版本 · 已安装 {installedCount} 个";
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
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

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DownloadSelectedVersionAsync()
    {
        if (!CanDownloadSelectedVersion || SelectedRemoteVersion is null) return;

        var item = SelectedRemoteVersion;

        if (!TryResolveVersionName(item.Id, loaderSelected: false, out var desiredName))
            return;

        var task = TryCreateTask(desiredName, DownloadTaskKind.Version, item.Id);
        if (task is null) return;

        AddVersionSteps(task);
        task.StatusText = "正在准备下载...";

        CurrentPage = AppPage.Download;
        EnterTaskSection(leaveVersionDetail: true);
        Status = $"正在下载 Minecraft {item.Id}...";
        LauncherLog.Info($"开始下载版本 {item.Id}（目标名称 {desiredName}）");

        var stage = MinecraftDownloadStage.Preparing;

        try
        {
            // 版本列表与文件下载共用「游戏版本」源。
            var mirror = ResolveGameMirror();

            var lastUiTicks = 0L;
            var progress = new Progress<MinecraftVersionDownloadProgress>(report =>
            {
                // 准备阶段没有百分比，直接把当前在拉的东西写在卡片上，避免看上去卡死。
                if (report.Stage == MinecraftDownloadStage.Preparing && report.CompletedFiles == 0)
                {
                    task.StatusText = $"正在获取{report.CurrentFile}…";
                    return;
                }

                var stageChanged = stage != report.Stage;
                var now = Environment.TickCount64;
                var isFinal = report.Percent is >= 100 ||
                              (report.TotalFiles > 0 && report.CompletedFiles >= report.TotalFiles);

                // 高并发下载会每文件上报一次，限制 UI 刷新频率，但最终状态一定更新。
                if (!stageChanged && !isFinal && now - lastUiTicks < 100)
                    return;

                lastUiTicks = now;

                if (stageChanged)
                {
                    stage = report.Stage;
                    ApplyVersionStage(task, stage);
                }

                task.CompletedFiles = report.CompletedFiles;
                task.TotalFiles = report.TotalFiles;

                var percent = report.Percent is double value ? $"（{value:0}%）" : string.Empty;
                var megabytes = report.BytesDownloaded / 1024.0 / 1024.0;
                var speed = task.SpeedMeter.Feed(report.BytesDownloaded);
                var speedText = speed.Length > 0 ? $"，{speed}" : string.Empty;

                // 并发下载时进度可能因文件完成顺序出现瞬时回落，进度条只允许前进。
                if (report.Percent is double currentPercent && currentPercent > task.Percent)
                    task.Percent = Math.Min(currentPercent, 99);

                if (stage == MinecraftDownloadStage.Files && report.Percent is double filePercent)
                    task.Steps[1].Detail = $"{filePercent:0}%";

                task.StatusText = $"{report.CompletedFiles}/{report.TotalFiles} 个文件，{megabytes:0.0} MB{speedText}";
                Status = report.TotalFiles > 0
                    ? $"正在下载 {item.Id}：{report.CompletedFiles}/{report.TotalFiles} 个文件，{megabytes:0.0} MB{speedText}{percent}"
                    : $"正在下载 {item.Id}...";
                NotifyDownloadStats();
            });

            // 下载本身是纯 IO / 校验，放到线程池：其中「文件已存在 → 校验并跳过」的路径
            // 几乎全是同步工作，留在 UI 线程会让整个窗口卡住（进度和输入都没反应）。
            var result = await Task.Run(
                () => MinecraftVersionDownloader.DownloadAsync(
                    MinecraftDirectory,
                    item.Entry,
                    progress,
                    new MinecraftVersionDownloadOptions
                    {
                        Mirror = mirror,
                    },
                    task.Token),
                task.Token);

            // 收尾（改名、重扫版本列表）也算任务时间，进度先停在 99，真正完成才打满。
            task.Percent = 99;
            NotifyDownloadStats();

            var finalId = VersionNaming.RenameInstalledVersion(
                MinecraftDirectory, result.VersionId, desiredName);

            // 只重扫版本：下载不会改变系统里的 Java，重新发现只会把收尾拖长。
            await RefreshCoreAsync(refreshJava: false);

            var local = Versions.FirstOrDefault(version =>
                string.Equals(version.Id, finalId, StringComparison.OrdinalIgnoreCase));
            if (local is not null) SelectedVersion = local;

            UpdateRemoteInstalledStatus();
            Status = $"版本 {finalId} 下载完成";
            LauncherLog.Info($"版本 {finalId} 下载完成");
            task.Percent = 100;
            FinishTask(task, DownloadTaskState.Completed, $"版本 {finalId} 下载完成");
        }
        catch (OperationCanceledException)
        {
            Status = "版本下载已取消";
            LauncherLog.Warn($"版本 {item.Id} 下载已取消");
            FinishTask(task, DownloadTaskState.Canceled, "下载已取消");
        }
        catch (Exception exception)
        {
            Status = "版本下载失败";
            LauncherLog.Error($"版本 {item.Id} 下载失败", exception);
            ShowDownloadError($"下载 Minecraft {item.Id}", exception);
            FinishTask(task, DownloadTaskState.Failed, "下载失败");
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task InstallSelectedLoaderAsync()
    {
        if (SelectedRemoteVersion is null ||
            _selectedLoaderKey is null ||
            _selectedLoaderVersion is null ||
            IsBusy || IsLaunching)
        {
            return;
        }

        var item = SelectedRemoteVersion;
        var loader = _selectedLoaderKey;
        var loaderVersion = _selectedLoaderVersion;
        var displayName = GetLoaderDisplayName(loader);

        if (!TryResolveVersionName(item.Id, loaderSelected: true, out var desiredName))
            return;

        var task = TryCreateTask(desiredName, DownloadTaskKind.Version, item.Id);
        if (task is null) return;

        AddVersionSteps(task);
        task.StatusText = "正在准备安装...";

        CurrentPage = AppPage.Download;
        EnterTaskSection(leaveVersionDetail: true);
        Status = $"正在安装 {displayName} {loaderVersion}...";
        LauncherLog.Info($"开始安装 {displayName} {loaderVersion} → {desiredName}");

        // 原版文件直接下载到最终版本目录（versions/{desiredName}），
        // 加载器安装只把配置合并进同一份 JSON，不会多出一个普通版本。
        const double vanillaPhase = 65.0;
        var installerRunning = false;
        var stage = MinecraftDownloadStage.Preparing;

        var creepTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        creepTimer.Tick += (_, _) =>
        {
            if (!installerRunning) return;
            if (task.Percent >= 97) return;

            // 安装器不提供百分比，用「逐步逼近 97%」推进：
            // 刚开始明显往前走，越接近上限越慢，不会像固定步长那样一分钟只动几个点。
            task.Percent = Math.Min(97, task.Percent + Math.Max(0.4, (97 - task.Percent) * 0.06));

            // 侧边栏的总进度也来自 task.Percent，这里不通知会和卡片进度条对不上。
            NotifyDownloadStats();
        };
        creepTimer.Start();

        try
        {
            // 每次安装都重新铺一遍原版文件：目标目录是新的，库与 assets 已存在时会被校验后跳过。
            var lastVanillaTicks = 0L;
            var vanillaProgress = new Progress<MinecraftVersionDownloadProgress>(report =>
            {
                if (report.Stage == MinecraftDownloadStage.Preparing && report.CompletedFiles == 0)
                {
                    task.StatusText = $"正在获取{report.CurrentFile}…";
                    return;
                }

                var stageChanged = stage != report.Stage;
                var now = Environment.TickCount64;
                var isFinal = report.Percent is >= 100 ||
                              (report.TotalFiles > 0 && report.CompletedFiles >= report.TotalFiles);

                if (!stageChanged && !isFinal && now - lastVanillaTicks < 100)
                    return;

                lastVanillaTicks = now;

                if (stageChanged)
                {
                    stage = report.Stage;
                    ApplyVersionStage(task, stage);
                }

                if (report.Percent is double percent)
                {
                    var mapped = percent * vanillaPhase / 100.0;
                    if (mapped > task.Percent) task.Percent = Math.Min(mapped, 99);

                    // 和普通版本下载一致，让“游戏文件”这一步也带上百分比。
                    if (stage == MinecraftDownloadStage.Files && task.Steps.Count > 1)
                        task.Steps[1].Detail = $"{percent:0}%";
                }

                if (report.BytesDownloaded > 0)
                    task.SpeedMeter.Feed(report.BytesDownloaded);

                task.CompletedFiles = report.CompletedFiles;
                task.TotalFiles = report.TotalFiles;
                task.StatusText = report.TotalFiles > 0
                    ? $"正在下载原版文件 {report.CompletedFiles}/{report.TotalFiles}..."
                    : "正在下载原版文件...";
                Status = task.StatusText;
                NotifyDownloadStats();
            });

            // 同理放到线程池：这一轮经常几乎全是「已存在 → 校验 → 跳过」的同步工作。
            await Task.Run(
                () => MinecraftVersionDownloader.DownloadAsync(
                    MinecraftDirectory,
                    item.Entry,
                    vanillaProgress,
                    new MinecraftVersionDownloadOptions
                    {
                        Mirror = ResolveGameMirror(),
                        TargetVersionId = desiredName,
                    },
                    task.Token),
                task.Token);

            // 原版文件下载结束后重新计量，安装器阶段的字节从 0 开始。
            task.SpeedMeter.Reset();
            NotifyDownloadStats();

            string? javaPath = null;

            if (loader is "forge" or "neoforge")
            {
                // 原版 json 现在就在目标版本目录里，用它的 javaVersion 选安装器用的 Java。
                var requiredJava = ReadRequiredJavaMajor(desiredName, loader);
                var runtimes = JavaRuntimes.Count > 0
                    ? (IEnumerable<JavaRuntime>)JavaRuntimes
                    : await JavaLocator.DiscoverAsync(
                        MinecraftDirectory, _settings.JavaPath, task.Token);

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

            var lastInstallTicks = 0L;
            var lastInstallBytes = 0L;
            var lastInstallText = string.Empty;
            var installProgress = new Progress<LoaderInstallProgress>(report =>
            {
                // 阶段文本变化时记一笔，事后能从日志看出每一步的进度值。
                if (report.Text != lastInstallText)
                {
                    lastInstallText = report.Text;
                    LauncherLog.Info($"安装阶段：{report.Text}（{report.Percent?.ToString("0.0") ?? "无百分比"}）");
                }
                // 下载回调很密集，限制界面刷新频率；阶段切换与开始/结束阶段一定刷新。
                var now = Environment.TickCount64;
                var isPhaseChange = report.Percent is null or >= 100;
                if (!isPhaseChange && now - lastInstallTicks < 100) return;
                lastInstallTicks = now;

                if (stage != report.Stage)
                {
                    stage = report.Stage;
                    ApplyVersionStage(task, stage);
                }

                // 安装器依赖与安装器 jar 的下载量累计上报，用于显示实时速度。
                if (report.BytesDownloaded is long bytes)
                {
                    // 每个阶段重新从 0 开始计数，先重置测速，否则速度会一瞬显示 0。
                    if (bytes < lastInstallBytes) task.SpeedMeter.Reset();
                    lastInstallBytes = bytes;
                    task.SpeedMeter.Feed(bytes);
                }

                if (report.Percent is double percent)
                {
                    installerRunning = false;
                    var mapped = vanillaPhase + percent * (100 - vanillaPhase) / 100.0;
                    if (mapped > task.Percent) task.Percent = Math.Min(mapped, 99);
                }
                else
                {
                    // Forge / NeoForge 安装器不提供百分比，由定时器推进进度条。
                    installerRunning = true;
                    task.SpeedMeter.Reset();
                }

                task.IsIndeterminate = false;

                task.StatusText = report.Text;
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

                task.StatusText = line;
                Status = line;
            });

            // 安装过程中有解压安装器、解析 JSON、拷贝库等同步工作，同样放线程池，避免界面卡住。
            var result = await Task.Run(
                () => LoaderInstaller.InstallAsync(
                    MinecraftDirectory,
                    item.Id,
                    loader,
                    loaderVersion,
                    new LoaderInstallOptions
                    {
                        FabricApiVersion = _selectedFabricApiVersion,
                        IsolateVersion = VersionIsolationDetector.Detect(
                            MinecraftDirectory,
                            desiredName,
                            item.Entry.Type,
                            modable: true,
                            _settings.DefaultIsolation).IsIsolated,
                        Mirror = ResolveLoaderMirror(loader),
                        JavaPath = javaPath,
                        VersionName = desiredName,
                        Progress = installProgress,
                        Log = installLog,
                    },
                    task.Token),
                task.Token);

            task.Percent = 99;
            NotifyDownloadStats();

            // 只重扫版本：安装加载器不会改变系统里的 Java。
            await RefreshCoreAsync(refreshJava: false);

            var local = Versions.FirstOrDefault(version =>
                string.Equals(version.Id, result.VersionId, StringComparison.OrdinalIgnoreCase));
            if (local is not null) SelectedVersion = local;

            Status = $"{displayName} {loaderVersion} 安装完成";
            LauncherLog.Info($"{displayName} {loaderVersion} 安装完成 → {result.VersionId}");
            task.Percent = 100;
            FinishTask(task, DownloadTaskState.Completed, $"{displayName} {loaderVersion} 安装完成");
        }
        catch (OperationCanceledException)
        {
            Status = "加载器安装已取消";
            LauncherLog.Warn($"{displayName} {loaderVersion} 安装已取消");
            FinishTask(task, DownloadTaskState.Canceled, "安装已取消");
        }
        catch (Exception exception)
        {
            Status = $"{displayName} {loaderVersion} 安装失败";
            LauncherLog.Error($"{displayName} {loaderVersion} 安装失败", exception);
            ShowDownloadError($"安装 {displayName} {loaderVersion}", exception);
            FinishTask(task, DownloadTaskState.Failed, "安装失败");
        }
        finally
        {
            creepTimer.Stop();
        }
    }

    /// <summary>读取原版 JSON 中要求的 Java 主版本；Forge 至少 8，NeoForge 至少 17。</summary>
    private int ReadRequiredJavaMajor(string versionId, string loader)
    {
        var fallback = loader == "neoforge" ? 17 : 8;

        try
        {
            var path = Path.Combine(
                MinecraftDirectory, "versions", versionId, versionId + ".json");
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
        var clock = Stopwatch.StartNew();

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

            LauncherLog.Info($"[PERF] Java 运行时列表处理：{_allJavaRuntimes.Count} 项，{clock.ElapsedMilliseconds} ms");

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

    [RelayCommand(AllowConcurrentExecutions = true)]
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
        if (requiredMajor <= 0) return;

        var task = TryCreateTask(label, DownloadTaskKind.Java, label);
        if (task is null) return;

        task.StatusText = "正在获取下载信息...";

        CurrentPage = AppPage.Download;
        EnterTaskSection(leaveVersionDetail: true);
        Status = $"正在获取 {label} 下载信息...";
        LauncherLog.Info($"开始下载 {label}");

        try
        {
            var mirror = ResolveGameMirror();

            var lastUiTicks = 0L;
            var progress = new Progress<JavaRuntimeDownloadProgress>(report =>
            {
                var now = Environment.TickCount64;
                var isFinal = report.Percent is >= 100 ||
                              (report.TotalFiles > 0 && report.CompletedFiles >= report.TotalFiles);

                if (!isFinal && now - lastUiTicks < 100)
                    return;

                lastUiTicks = now;

                task.CompletedFiles = report.CompletedFiles;
                task.TotalFiles = report.TotalFiles;

                var percent = report.Percent is double value ? $"（{value:0}%）" : string.Empty;
                var megabytes = report.BytesDownloaded / 1024.0 / 1024.0;
                var speed = task.SpeedMeter.Feed(report.BytesDownloaded);
                var speedText = speed.Length > 0 ? $"，{speed}" : string.Empty;
                task.Percent = report.Percent ?? 0;
                task.StatusText = $"{report.CompletedFiles}/{report.TotalFiles} 个文件，{megabytes:0.0} MB{speedText}";
                Status = report.TotalFiles > 0
                    ? $"正在下载 {label}：{report.CompletedFiles}/{report.TotalFiles} 个文件，{megabytes:0.0} MB{speedText}{percent}"
                    : $"正在下载 {label}...";
                NotifyDownloadStats();
            });

            var result = await Task.Run(
                () => JavaRuntimeDownloader.DownloadAsync(
                    MinecraftDirectory,
                    component,
                    requiredMajor,
                    progress,
                    new JavaRuntimeDownloadOptions
                    {
                        Mirror = mirror,
                    },
                    task.Token),
                task.Token);

            var runtime = await JavaLocator.ProbeAsync(
                result.JavaExecutablePath, MinecraftDirectory, _lifetime.Token);

            if (runtime is null)
            {
                Status = $"{label} 下载完成，但无法运行";
                FinishTask(task, DownloadTaskState.Failed, "下载完成，但无法运行");
                return;
            }

            var existing = JavaRuntimes.FirstOrDefault(item =>
                string.Equals(item.Path, runtime.Path, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
                JavaRuntimes.Add(runtime);
            else
                runtime = existing;

            SelectedJava = runtime;
            task.Percent = 100;
            Status = $"{label} 下载完成";
            LauncherLog.Info($"{label} 下载完成：{runtime.Path}");

            foreach (var item in _allJavaRuntimes)
            {
                if (string.Equals(item.ComponentName, result.ComponentName, StringComparison.OrdinalIgnoreCase))
                    item.IsInstalled = true;
            }
            UpdateJavaRuntimeCatalogStatus();

            FinishTask(task, DownloadTaskState.Completed, $"已安装 {label}");
        }
        catch (OperationCanceledException)
        {
            Status = "Java 下载已取消";
            LauncherLog.Warn($"{label} 下载已取消");
            FinishTask(task, DownloadTaskState.Canceled, "下载已取消");
        }
        catch (Exception exception)
        {
            Status = "Java 下载失败";
            LauncherLog.Error($"{label} 下载失败", exception);
            ShowDownloadError($"下载 {label}", exception);
            FinishTask(task, DownloadTaskState.Failed, "下载失败");
        }
        finally
        {
            UpdateJavaRuntimeInstalledStatus();
        }
    }

    /// <summary>游戏启动后启动器窗口需要做何处理（由主窗口订阅执行）。</summary>
    public event EventHandler<LauncherVisibilityAfterLaunch>? GameStarted;

    /// <summary>游戏退出后通知主窗口恢复显示。</summary>
    public event EventHandler? GameExited;

    private int _launcherWindowWidth;
    private int _launcherWindowHeight;
    private int _primaryScreenWidth;
    private int _primaryScreenHeight;

    /// <summary>主窗口把自身尺寸与主屏幕工作区同步给启动设置使用。</summary>
    public void UpdateLauncherWindowMetrics(
        double windowWidth,
        double windowHeight,
        double screenWidth,
        double screenHeight)
    {
        _launcherWindowWidth = (int)Math.Round(windowWidth);
        _launcherWindowHeight = (int)Math.Round(windowHeight);
        _primaryScreenWidth = (int)Math.Round(screenWidth);
        _primaryScreenHeight = (int)Math.Round(screenHeight);
    }

    /// <summary>按「窗口大小」设置计算游戏窗口尺寸，并返回是否全屏。</summary>
    private (int Width, int Height, bool Fullscreen) ResolveGameWindow()
    {
        switch (_settings.GameWindowSize)
        {
            case GameWindowSizeMode.Fullscreen:
                return (854, 480, true);

            case GameWindowSizeMode.SameAsLauncher:
                return (
                    _launcherWindowWidth > 0 ? _launcherWindowWidth : 854,
                    _launcherWindowHeight > 0 ? _launcherWindowHeight : 480,
                    false);

            case GameWindowSizeMode.Custom:
                return (_settings.GameWindowWidth, _settings.GameWindowHeight, false);

            case GameWindowSizeMode.Maximized:
                return (
                    _primaryScreenWidth > 0 ? _primaryScreenWidth : 1280,
                    _primaryScreenHeight > 0 ? _primaryScreenHeight : 720,
                    false);

            default:
                return (854, 480, false);
        }
    }

    [RelayCommand]
    private async Task LaunchAsync()
    {
        if (!CanLaunch || _resolved is null || SelectedJava is null) return;

        IsLaunching = true;
        Status = "正在启动...";

        var started = false;

        try
        {
            var (width, height, fullscreen) = ResolveGameWindow();
            var options = new LaunchOptions(
                PlayerName, MemoryMb, IsVersionIsolated, width, height, fullscreen);
            var plan = LaunchPlanBuilder.Build(_resolved, SelectedJava, options);

            LauncherLog.Info($"启动游戏：{_resolved.Id}（Java {SelectedJava.MajorVersion}）");

            // 游戏输出直接在当前线程写入日志，不占用 UI 线程。
            var gameLog = new InlineProgress<string>(LauncherLog.Game);
            var visibility = _settings.LauncherVisibility;

            var exitCode = await GameLauncher.RunAsync(
                plan,
                gameLog,
                _settings.GameProcessPriority,
                () =>
                {
                    started = true;
                    GameStarted?.Invoke(this, visibility);
                },
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

            if (started)
                GameExited?.Invoke(this, EventArgs.Empty);
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

        LauncherLog.Info($"Java 发现完成：{JavaRuntimes.Count} 个运行时");

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
            OnPropertyChanged(nameof(IsolationDescription));
            UpdateLaunchWarning();
            return;
        }

        VersionDetails = "正在解析版本...";
        _resolved = null;
        UpdateLaunchWarning();

        try
        {
            var resolved = await Task.Run(
                () => VersionResolver.Resolve(MinecraftDirectory, item.Id, _settings.DefaultIsolation),
                _lifetime.Token);
            if (revision != _detailsRevision) return;

            _resolved = resolved;
            IsVersionIsolated = resolved.Isolation.IsIsolated;
            OnPropertyChanged(nameof(EffectiveGameDirectory));
            OnPropertyChanged(nameof(IsolationDescription));
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
            OnPropertyChanged(nameof(IsolationDescription));
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
            _remoteSearchTimer.Stop();
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
