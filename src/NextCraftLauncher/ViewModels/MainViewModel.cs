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
using Avalonia.Media;
using Avalonia.Threading;
using NextCraftLauncher.Core.Auth;
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
using NextCraftLauncher.Icons;
using NextCraftLauncher.Theme;

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
    private bool _backgroundWorkStarted;
    private int _activeTaskCount;
    private readonly LoaderVersionPrefetcher _loaderPrefetcher;
    private StoredAccount? _account;
    private CancellationTokenSource? _loginCts;

    public MainViewModel()
    {
        _settings = AppSettings.Load();
        _minecraftDirectory = _settings.MinecraftDirectory;
        ModCatalog.CurseForgeApiKey = _settings.CurseForgeApiKey;
        _playerName = _settings.PlayerName;
        _account = AccountStore.Load();
        _isMicrosoftAccount = string.Equals(_settings.AccountType, "microsoft", StringComparison.OrdinalIgnoreCase);
        ApplyAccount();
        _memoryMb = MemoryOptions.Contains(_settings.MemoryMb) ? _settings.MemoryMb : 4096;
        _openFolderAfterModDownload = _settings.OpenFolderAfterModDownload;
        _jumpToTasksAfterModDownload = _settings.JumpToTasksAfterModDownload;
        _selectedModDefaultFilterIndex = _settings.ModDetailStartsWithRecommend ? 1 : 0;
        _openFolderAfterResourcePackDownload = _settings.OpenFolderAfterResourcePackDownload;
        _jumpToTasksAfterResourcePackDownload = _settings.JumpToTasksAfterResourcePackDownload;
        _selectedResourcePackDefaultFilterIndex =
            _settings.ResourcePackDetailStartsWithRecommend ? 1 : 0;
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

        _lastVersionSaveTimer.Tick += (_, _) =>
        {
            _lastVersionSaveTimer.Stop();
            _settings.Save();
        };

        LoadAccentOptions();
    }

    /// <summary>主题可选颜色（外观页）。</summary>
    public ObservableCollection<AccentOptionViewModel> AccentOptions { get; } = new();

    /// <summary>「跟随系统」选项：单独一行显示，圆形色块里是系统强调色。</summary>
    public AccentOptionViewModel SystemAccentOption { get; private set; } = null!;

    private static readonly (string Name, string Hex)[] AccentPresets =
    {
        ("蓝", "#0078D4"),
        ("青", "#00B7C3"),
        ("绿", "#107C10"),
        ("橄榄", "#498205"),
        ("金", "#C77B00"),
        ("橙", "#CA5010"),
        ("红", "#D13438"),
        ("紫", "#8764B8"),
        ("品红", "#B146C2"),
        ("玫红", "#E3008C"),
    };

    /// <summary>建好可选项，并按保存的主题色标记选中项。</summary>
    private void LoadAccentOptions()
    {
        foreach (var (name, hex) in AccentPresets)
            AccentOptions.Add(new AccentOptionViewModel(name, Color.Parse(hex), SetAccent));

        SystemAccentOption = new AccentOptionViewModel("跟随系统", Palette.SystemAccent, SetAccent, isSystem: true);

        var saved = _settings.ThemeAccent;
        var selected = AccentOptions.FirstOrDefault(option =>
            string.Equals("#" + (option.Color.ToUInt32() & 0xFFFFFF).ToString("X6"), saved, StringComparison.Ordinal));

        if (selected is null)
        {
            SystemAccentOption.IsSelected = true;
            return;
        }

        selected.IsSelected = true;
        Palette.ApplyAccent(selected.Color);
    }

    /// <summary>外观页选中主题色：标记、写配置、立即换色。</summary>
    private void SetAccent(AccentOptionViewModel option)
    {
        foreach (var item in AccentOptions) item.IsSelected = ReferenceEquals(item, option);
        SystemAccentOption.IsSelected = option.IsSystem;

        _settings.ThemeAccent = option.IsSystem
            ? null
            : "#" + (option.Color.ToUInt32() & 0xFFFFFF).ToString("X6");
        _settings.Save();

        Palette.ApplyAccent(option.IsSystem ? null : option.Color);
    }

    /// <summary>
    /// 启动界面期间完成本地初始化：扫描版本、发现 Java，并先填上缓存的远程清单。
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
    }

    /// <summary>
    /// 主窗口画好之后才开始联网预取，避免窗口刚出现时后台任务抢占 UI 线程。
    /// </summary>
    public void StartBackgroundWork()
    {
        if (_backgroundWorkStarted) return;
        _backgroundWorkStarted = true;

        _ = PrefetchRemoteVersionsAsync();
        _ = Task.Run(RunLoaderPrefetcherAsync);

        // 与远程版本清单同时开始预取默认的 Mod 列表，打开下载页时直接有内容。
        _ = PrefetchModsAsync();

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

    /// <summary>启动时按当前选中的版本预取第一页 Mod（优先读缓存）。</summary>
    private async Task PrefetchModsAsync()
    {
        if (_modPageInitialized) return;
        _modPageInitialized = true;
        _modPageType = ModProjectType.Mod;

        try
        {
            UpdateModFiltersFromSelection();
            await FetchModPageAsync(1, newSearch: true, ignoreCache: false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            LauncherLog.Warn($"预取 Mod 列表失败：{exception.Message}");
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

    /// <summary>版本详情页顶部的邻近版本（最多 6 个，不含当前版本）。</summary>
    public RangeObservableCollection<RemoteVersionItemViewModel> RelatedRemoteVersions { get; } = new();

    public bool HasRelatedRemoteVersions => RelatedRemoteVersions.Count > 0;

    /// <summary>模组详情页顶部的版本条：全部 + 该模组支持过的各游戏版本。</summary>
    public RangeObservableCollection<ModVersionFilterViewModel> ModVersionFilters { get; } = new();

    public bool ShowModVersionFilters => ModVersionFilters.Count > 1;

    /// <summary>版本条「推荐」的标识值，与普通版本线区分。</summary>
    private const string RecommendFilter = "\u0001recommend";

    private readonly List<ModVersionItemViewModel> _allModVersions = new();
    private string? _selectedModVersionFilter;

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

    /// <summary>列表当前实际显示的版本（类型 + 搜索过滤后），侧栏的邻近版本也从这里取。</summary>
    private readonly List<RemoteVersionItemViewModel> _filteredRemoteVersions = new();
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

    /// <summary>版本选择变化很频繁（滚轮切轮播），合并成一次写盘。</summary>
    private readonly DispatcherTimer _lastVersionSaveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(600),
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

    /// <summary>Mod 详情页进入时的默认版本筛选。</summary>
    public IReadOnlyList<string> ModDefaultFilterOptions { get; } = new[]
    {
        "全部",
        "推荐",
    };

    [ObservableProperty] private string _minecraftDirectory = string.Empty;
    [ObservableProperty] private VersionItemViewModel? _selectedVersion;
    [ObservableProperty] private GameFolderItemViewModel? _selectedGameFolder;
    [ObservableProperty] private JavaRuntime? _selectedJava;
    [ObservableProperty] private string _playerName = "Player";
    [ObservableProperty] private bool _isMicrosoftAccount;
    [ObservableProperty] private bool _hasMicrosoftAccount;
    [ObservableProperty] private string _accountDisplayName = "未登录";
    [ObservableProperty] private string _accountSubtitle = "";
    [ObservableProperty] private bool _isLoginDialogOpen;
    [ObservableProperty] private bool _isLoggingIn;
    [ObservableProperty] private string _loginUserCode = string.Empty;
    [ObservableProperty] private string _loginVerificationUri = string.Empty;
    [ObservableProperty] private string _loginStatusText = string.Empty;
    [ObservableProperty] private int _memoryMb = 4096;

    /// <summary>Mod 下载完成后自动打开所在文件夹。</summary>
    [ObservableProperty] private bool _openFolderAfterModDownload;

    /// <summary>Mod 下载开始后切到下载任务页。</summary>
    [ObservableProperty] private bool _jumpToTasksAfterModDownload;

    /// <summary>进入 Mod 详情页时默认选中的版本筛选（0 = 全部，1 = 推荐）。</summary>
    [ObservableProperty] private int _selectedModDefaultFilterIndex;

    /// <summary>材质包下载完成后自动打开所在文件夹。</summary>
    [ObservableProperty] private bool _openFolderAfterResourcePackDownload;

    /// <summary>材质包下载开始后切到下载任务页。</summary>
    [ObservableProperty] private bool _jumpToTasksAfterResourcePackDownload;

    /// <summary>进入材质包详情页时默认选中的版本筛选（0 = 全部，1 = 推荐）。</summary>
    [ObservableProperty] private int _selectedResourcePackDefaultFilterIndex;
    [ObservableProperty] private string _status = "就绪";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isLaunching;
    [ObservableProperty] private string _versionsCountText = "尚未扫描";
    [ObservableProperty] private string _versionDetails = "请选择左侧版本。";

    // 「版本设置」页查看的版本，与启动版本分开：
    // 打开设置不会改变启动页显示的版本，启动页的滚轮也不会影响设置页。
    [ObservableProperty] private VersionItemViewModel? _versionSettingsVersion;
    [ObservableProperty] private string _versionSettingsDetails = string.Empty;
    private int _versionSettingsRevision;
    [ObservableProperty] private string? _launchWarning;
    [ObservableProperty] private bool _isVersionIsolated;
    [ObservableProperty] private string _remoteVersionSearch = string.Empty;
    [ObservableProperty] private string _remoteVersionType = "正式版";
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
    [ObservableProperty] private bool _isJavaRuntimeCatalogFailed;
    [ObservableProperty] private string _javaRuntimeCatalogStatus = "尚未加载 Java 运行时列表";
    [ObservableProperty] private AppPage _currentPage = AppPage.Launch;
    [ObservableProperty] private int _selectedGameMirrorIndex;
    [ObservableProperty] private int _selectedForgeMirrorIndex;
    [ObservableProperty] private int _selectedFabricMirrorIndex;
    [ObservableProperty] private int _selectedNeoForgeMirrorIndex;
    [ObservableProperty] private LaunchSection _currentLaunchSection = LaunchSection.Start;
    [ObservableProperty] private VersionSettingsSection _currentVersionSettingsSection = VersionSettingsSection.Settings;
    [ObservableProperty] private DownloadSection _currentDownloadSection = DownloadSection.Versions;
    [ObservableProperty] private SettingsSection _currentSettingsSection = SettingsSection.Launch;
    [ObservableProperty] private bool _isVersionDeleteDialogOpen;
    [ObservableProperty] private string _versionDeleteDialogMessage = string.Empty;

    /// <summary>启动页的版本详情页（目前只显示版本信息）是否打开。</summary>
    [ObservableProperty] private bool _isVersionDetailOpen;

    /// <summary>打开详情页之前所在的区块，用来决定「返回」回到哪里。</summary>
    private LaunchSection _sectionBeforeVersionDetail = LaunchSection.Versions;

    // 下载/安装失败时的置顶错误弹窗。
    [ObservableProperty] private bool _isDownloadErrorDialogOpen;
    [ObservableProperty] private string _downloadErrorTitle = string.Empty;
    [ObservableProperty] private string _downloadErrorHint = string.Empty;
    private Exception? _downloadErrorException;
    private string _downloadErrorContext = string.Empty;

    // 当前版本不能启动时用 notice 卡片说明原因。
    [ObservableProperty] private bool _isLaunchNoticeOpen;
    [ObservableProperty] private string _launchNoticeTitle = string.Empty;
    [ObservableProperty] private string _launchNoticeMessage = string.Empty;

    [RelayCommand]
    private void CloseLaunchNotice() => IsLaunchNoticeOpen = false;

    /// <summary>任意一张提示卡片是否打开；背景模糊按它驱动。</summary>
    public bool IsMessageCardOpen => IsDownloadErrorDialogOpen || IsLaunchNoticeOpen;

    partial void OnIsDownloadErrorDialogOpenChanged(bool value)
        => OnPropertyChanged(nameof(IsMessageCardOpen));

    partial void OnIsLaunchNoticeOpenChanged(bool value)
        => OnPropertyChanged(nameof(IsMessageCardOpen));

    private void ShowLaunchNotice(string message, string title = "无法启动")
    {
        LaunchNoticeTitle = title;
        LaunchNoticeMessage = message;
        IsLaunchNoticeOpen = true;
    }

    /// <summary>通用的提示卡片（与启动提示共用同一张卡）。</summary>
    private void ShowNotice(string title, string message) => ShowLaunchNotice(message, title);

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

    public bool IsVersionSettingsSelected => CurrentVersionSettingsSection == VersionSettingsSection.Settings;

    public bool IsVersionModsSelected => CurrentVersionSettingsSection == VersionSettingsSection.Mods;

    public bool IsVersionExportSelected => CurrentVersionSettingsSection == VersionSettingsSection.Export;

    /// <summary>版本设置页是否打开（标题栏显示版本名）。</summary>
    public bool IsVersionSettingsOpen => IsLaunchPageSelected && IsVersionDetailOpen;

    public bool IsSettingsLaunchSelected => CurrentSettingsSection == SettingsSection.Launch;

    public bool IsSettingsDownloadSelected => CurrentSettingsSection == SettingsSection.Download;

    public bool IsSettingsAppearanceSelected => CurrentSettingsSection == SettingsSection.Appearance;

    public bool IsDownloadVersionsSelected => CurrentDownloadSection == DownloadSection.Versions;

    public bool IsDownloadModsSelected => CurrentDownloadSection == DownloadSection.Mods;

    public bool IsDownloadResourcePacksSelected => CurrentDownloadSection == DownloadSection.ResourcePacks;

    /// <summary>Mod 与材质包共用同一个页面。</summary>
    public bool IsDownloadContentSelected =>
        CurrentDownloadSection is DownloadSection.Mods or DownloadSection.ResourcePacks;

    /// <summary>当前内容页是 Mod 还是材质包（不在内容页时按 Mod 处理）。</summary>
    private ModProjectType CurrentContentType =>
        CurrentDownloadSection == DownloadSection.ResourcePacks
            ? ModProjectType.ResourcePack
            : ModProjectType.Mod;

    public bool IsModSearchLoaderVisible => CurrentContentType == ModProjectType.Mod;

    public string ModSearchPlaceholder => CurrentContentType == ModProjectType.ResourcePack
        ? "搜索材质包…"
        : "搜索 Mod…";

    public bool IsDownloadJavaSelected => CurrentDownloadSection == DownloadSection.Java;

    public bool IsDownloadRunningSelected => CurrentDownloadSection == DownloadSection.Running;

    /// <summary>当前在某个子页面（标题栏显示返回按钮）。</summary>
    public bool IsSubPageOpen =>
        (IsLaunchPageSelected && CurrentLaunchSection != LaunchSection.Start)
        || (IsDownloadPageSelected &&
            (IsRemoteVersionDetailOpen || IsModDetailOpen || IsDownloadRunningSelected));

    /// <summary>任务页当前有卡片可显示（含刚结束、等待自动移除的任务）。</summary>
    public bool HasDownloadTasks => DownloadTasks.Count > 0;

    public bool HasActiveDownloads => DownloadTasks.Any(task => task.IsRunning);

    public bool ShowRemoteVersionsLoading => IsRemoteVersionsLoading && RemoteVersionRows.Count == 0;

    /// <summary>列表为空时才显示加载提示；有缓存内容时不打扰。</summary>
    public bool ShowJavaRuntimeCatalogLoading
        => IsJavaRuntimeCatalogLoading && _allJavaRuntimes.Count == 0;

    partial void OnIsJavaRuntimeCatalogLoadingChanged(bool value)
        => OnPropertyChanged(nameof(ShowJavaRuntimeCatalogLoading));

    public string CurrentVersionName => SelectedVersion?.Id ?? "未选择版本";

    public string CurrentVersionSubtitle => SelectedVersion?.Subtitle ?? "在左侧「版本」中选择一个版本";


    /// <summary>当前版本图标用哪个图标：加载器优先，其次看原版版本类型。</summary>
    public string CurrentVersionBlock => VersionIcon.NameFor(SelectedVersion?.Summary);

    public string CurrentJavaName => SelectedJava?.DisplayName ?? "未选择 Java";

    public string RequiredJavaDescription => _resolved is null
        ? "请选择版本"
        : _resolved.Merged.JavaVersion is { Component: { Length: > 0 } component }
            ? $"Java {_resolved.RequiredJavaMajor}+（{component}）"
            : $"Java {_resolved.RequiredJavaMajor}+（自动选择组件）";

    public string EffectiveGameDirectory => _resolved is null
        ? "-"
        : IsVersionIsolated
            ? _resolved.VersionDirectory
            : _resolved.MinecraftDirectory;

    public bool CanLaunch =>
        !IsBusy &&
        !IsLaunching &&
        (!IsMicrosoftAccount || HasMicrosoftAccount) &&
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

    /// <summary>要创建的版本名已存在：按钮变灰，但仍可点击并弹出说明。</summary>
    public bool HasDuplicateVersionName
    {
        get
        {
            var name = VersionNaming.Normalize(VersionName);
            if (name.Length == 0) return false;

            return Versions.Any(version =>
                string.Equals(version.Id, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>下载详情页的大图标：选了加载器时用加载器图标，否则按原版版本类型。</summary>
    public string DownloadIconName =>
        VersionIcon.NameForLoader(_selectedLoaderKey)
        ?? VersionIcon.NameForType(SelectedRemoteVersion?.Type);

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
        OnPropertyChanged(nameof(CurrentVersionBlock));
        OnPropertyChanged(nameof(CanManageSelectedVersion));
        _detailsTask = LoadDetailsAsync(value);

        if (value is not null) RememberVersion(MinecraftDirectory, value.Id);
    }

    /// <summary>当前游戏文件夹上次选中的版本 Id；没记录过时返回 null。</summary>
    private string? RememberedVersionId(string directory)
    {
        var normalized = AppSettings.NormalizePath(directory);
        if (normalized is null) return null;

        foreach (var pair in _settings.LastVersions)
        {
            if (string.Equals(pair.Key, normalized, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return null;
    }

    /// <summary>记住这个文件夹选中的版本，下次启动或切回该文件夹时恢复。</summary>
    private void RememberVersion(string directory, string versionId)
    {
        var normalized = AppSettings.NormalizePath(directory);
        if (normalized is null) return;
        if (string.Equals(RememberedVersionId(normalized), versionId, StringComparison.Ordinal)) return;

        foreach (var stale in _settings.LastVersions.Keys
                     .Where(key => string.Equals(key, normalized, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            _settings.LastVersions.Remove(stale);
        }

        _settings.LastVersions[normalized] = versionId;

        _lastVersionSaveTimer.Stop();
        _lastVersionSaveTimer.Start();
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
        OnPropertyChanged(nameof(IsSubPageOpen));
    }

    partial void OnCurrentSettingsSectionChanged(SettingsSection value)
    {
        OnPropertyChanged(nameof(IsSettingsLaunchSelected));
        OnPropertyChanged(nameof(IsSettingsDownloadSelected));
        OnPropertyChanged(nameof(IsSettingsAppearanceSelected));
    }

    partial void OnCurrentVersionSettingsSectionChanged(VersionSettingsSection value)
    {
        OnPropertyChanged(nameof(IsVersionSettingsSelected));
        OnPropertyChanged(nameof(IsVersionModsSelected));
        OnPropertyChanged(nameof(IsVersionExportSelected));
    }

    partial void OnIsVersionDetailOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSubPageOpen));
        OnPropertyChanged(nameof(IsVersionSettingsOpen));
    }

    partial void OnIsRemoteVersionDetailOpenChanged(bool value)
        => OnPropertyChanged(nameof(IsSubPageOpen));

    partial void OnIsModDetailOpenChanged(bool value)
        => OnPropertyChanged(nameof(IsSubPageOpen));

    partial void OnCurrentDownloadSectionChanged(DownloadSection value)
    {
        OnPropertyChanged(nameof(IsDownloadVersionsSelected));
        OnPropertyChanged(nameof(IsDownloadModsSelected));
        OnPropertyChanged(nameof(IsDownloadResourcePacksSelected));
        OnPropertyChanged(nameof(IsDownloadContentSelected));
        OnPropertyChanged(nameof(IsModSearchLoaderVisible));
        OnPropertyChanged(nameof(ModSearchPlaceholder));
        OnPropertyChanged(nameof(IsDownloadJavaSelected));
        OnPropertyChanged(nameof(IsDownloadRunningSelected));
        OnPropertyChanged(nameof(IsSubPageOpen));

        if (value is DownloadSection.Mods or DownloadSection.ResourcePacks)
        {
            EnterContentSection(value == DownloadSection.ResourcePacks
                ? ModProjectType.ResourcePack
                : ModProjectType.Mod);
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
    public void Dispose()
    {
        try
        {
            _remoteSearchTimer.Stop();
            _lastVersionSaveTimer.Stop();
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
