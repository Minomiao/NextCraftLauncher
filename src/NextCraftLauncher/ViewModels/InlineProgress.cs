using System;

namespace NextCraftLauncher.ViewModels;

/// <summary>
/// 同步执行回调的 <see cref="IProgress{T}"/>。
/// 游戏输出逐行写入日志时不需要回到 UI 线程，避免大量 Post 影响界面响应。
/// </summary>
internal sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;

    public InlineProgress(Action<T> handler)
    {
        _handler = handler;
    }

    public void Report(T value) => _handler(value);
}
