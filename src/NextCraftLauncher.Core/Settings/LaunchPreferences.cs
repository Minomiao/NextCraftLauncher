namespace NextCraftLauncher.Core.Settings;

/// <summary>没有单独设置隔离方式的版本，默认采用哪种策略（对应 PCL2 的全局 0–4 模式）。</summary>
public enum DefaultIsolationMode
{
    /// <summary>关闭版本隔离。</summary>
    Off = 0,

    /// <summary>只隔离可安装 Mod 的版本。</summary>
    Moddable = 1,

    /// <summary>只隔离非正式版。</summary>
    NonRelease = 2,

    /// <summary>隔离可安装 Mod 的版本与非正式版。</summary>
    ModdableOrNonRelease = 3,

    /// <summary>隔离所有版本。</summary>
    All = 4,
}

/// <summary>游戏启动后启动器窗口的处理方式。</summary>
public enum LauncherVisibilityAfterLaunch
{
    Keep = 0,
    Minimize = 1,
    Hide = 2,
}

/// <summary>游戏窗口大小。</summary>
public enum GameWindowSizeMode
{
    Fullscreen = 0,
    Default = 1,
    SameAsLauncher = 2,
    Custom = 3,
    Maximized = 4,
}

/// <summary>游戏进程优先级。</summary>
public enum GameProcessPriority
{
    Low = 0,
    BelowNormal = 1,
    Normal = 2,
    AboveNormal = 3,
    High = 4,
}
