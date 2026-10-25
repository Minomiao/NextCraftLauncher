using System;

namespace NextCraftLauncher.ViewModels;

/// <summary>
/// 把累计字节数换算成平滑后的即时下载速度文本。
/// </summary>
internal sealed class TransferSpeedMeter
{
    private long _lastBytes;
    private long _lastTicks;
    private double _smoothed;
    private bool _started;

    public string Current => _smoothed <= 0
        ? string.Empty
        : $"{_smoothed / 1024.0 / 1024.0:0.0} MB/s";

    public double MegabytesPerSecond => _smoothed / 1024.0 / 1024.0;

    public string Feed(long bytes)
    {
        var now = Environment.TickCount64;

        if (!_started)
        {
            _started = true;
            _lastBytes = bytes;
            _lastTicks = now;
            return Current;
        }

        var elapsed = (now - _lastTicks) / 1000.0;
        if (elapsed < 0.5) return Current;

        var delta = Math.Max(0, bytes - _lastBytes);
        var speed = delta / elapsed;

        // 指数平滑，避免瞬时抖动让数字乱跳。
        _smoothed = _smoothed <= 0 ? speed : _smoothed * 0.6 + speed * 0.4;
        _lastBytes = bytes;
        _lastTicks = now;

        return Current;
    }

    public void Reset()
    {
        _started = false;
        _lastBytes = 0;
        _lastTicks = 0;
        _smoothed = 0;
    }
}
