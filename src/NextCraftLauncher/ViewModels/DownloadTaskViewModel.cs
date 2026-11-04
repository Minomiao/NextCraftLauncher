using System;
using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NextCraftLauncher.ViewModels;

public enum DownloadTaskKind
{
    Version,
    Java,
    Generic,
}

public enum DownloadTaskState
{
    Running,
    Completed,
    Canceled,
    Failed,
}

public enum DownloadStepState
{
    Pending,
    Active,
    Done,
}

/// <summary>
/// 任务卡片里的一行步骤：已完成显示对勾，进行中显示百分比（或省略号），未开始显示圆点。
/// </summary>
public partial class DownloadTaskStepViewModel : ObservableObject
{
    public DownloadTaskStepViewModel(string title)
    {
        Title = title;
    }

    public string Title { get; }

    [ObservableProperty] private DownloadStepState _state = DownloadStepState.Pending;

    /// <summary>进行中时显示在步骤左侧的文本；没有具体数值的步骤用省略号。</summary>
    [ObservableProperty] private string _detail = "…";

    public bool IsDone => State == DownloadStepState.Done;

    public bool IsActive => State == DownloadStepState.Active;

    public bool IsPending => State == DownloadStepState.Pending;

    partial void OnStateChanged(DownloadStepState value)
    {
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsPending));
    }

    public void SetState(DownloadStepState state, string? detail = null)
    {
        if (detail is not null)
            Detail = detail;
        State = state;
    }
}

/// <summary>
/// 任务页里的一张卡片。每次下载/安装都新建一个实例，多个任务互不干扰。
/// </summary>
public partial class DownloadTaskViewModel : ObservableObject
{
    private readonly CancellationTokenSource _cancellation;

    public DownloadTaskViewModel(string title, string key, DownloadTaskKind kind, CancellationToken lifetimeToken)
    {
        Title = title;
        Key = key;
        Kind = kind;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
    }

    public string Title { get; }

    /// <summary>去重键：同一个键的任务同时只允许一个（版本任务用原版版本号，Java 任务用显示名）。</summary>
    public string Key { get; }

    public DownloadTaskKind Kind { get; }

    public CancellationToken Token => _cancellation.Token;

    internal TransferSpeedMeter SpeedMeter { get; } = new();

    public ObservableCollection<DownloadTaskStepViewModel> Steps { get; } = new();

    public IRelayCommand CancelCommand { get; }

    [ObservableProperty] private DownloadTaskState _state = DownloadTaskState.Running;

    [ObservableProperty] private double _percent;

    [ObservableProperty] private string _statusText = "正在准备…";

    [ObservableProperty] private bool _isIndeterminate;

    [ObservableProperty] private int _completedFiles;

    [ObservableProperty] private int _totalFiles;

    public bool IsRunning => State == DownloadTaskState.Running;

    public bool IsFinished => !IsRunning;

    public bool IsFailed => State == DownloadTaskState.Failed;

    public bool HasSteps => Steps.Count > 0;

    public int RemainingFiles => Math.Max(0, TotalFiles - CompletedFiles);

    public string PercentText => IsIndeterminate ? "安装中…" : $"{Percent:0}%";

    public DownloadTaskStepViewModel AddStep(string title)
    {
        var step = new DownloadTaskStepViewModel(title);
        Steps.Add(step);
        OnPropertyChanged(nameof(HasSteps));
        return step;
    }

    /// <summary>请求取消。任务结束时 CTS 会被释放，因此这里吞掉已释放的情况。</summary>
    public void Cancel()
    {
        if (!IsRunning) return;

        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 任务刚好结束，忽略。
        }
    }

    public void Dispose() => _cancellation.Dispose();

    partial void OnStateChanged(DownloadTaskState value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(IsFailed));
        CancelCommand.NotifyCanExecuteChanged();
    }

    partial void OnPercentChanged(double value) => OnPropertyChanged(nameof(PercentText));

    partial void OnIsIndeterminateChanged(bool value) => OnPropertyChanged(nameof(PercentText));
}
