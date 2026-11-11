using System.Diagnostics;
using System.Text;
using NextCraftLauncher.Core.Settings;

namespace NextCraftLauncher.Core.Launch;

public static class GameLauncher
{
    /// <summary>
    /// 启动 Java 进程，并把标准输出/错误逐行转发给日志回调。
    /// </summary>
    public static async Task<int> RunAsync(
        LaunchPlan plan,
        IProgress<string>? log = null,
        GameProcessPriority priority = GameProcessPriority.Normal,
        Action? started = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(plan.JavaPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = plan.WorkingDirectory,
        };

        // 中文系统的 Java 8 输出 GBK、新版本输出 UTF-8，弄错就会满屏乱码。
        var outputEncoding = ProcessOutputEncoding.For(plan.JavaMajor);
        startInfo.StandardOutputEncoding = outputEncoding;
        startInfo.StandardErrorEncoding = outputEncoding;

        foreach (var argument in plan.Arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null) log?.Report(eventArgs.Data);
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null) log?.Report(eventArgs.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException("无法启动 Java 进程。");

        ApplyPriority(process, priority);
        started?.Invoke();

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            throw;
        }

        // 确保异步输出事件全部送达。
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>调整游戏进程优先级；权限不足时保持系统默认，不影响启动。</summary>
    private static void ApplyPriority(Process process, GameProcessPriority priority)
    {
        try
        {
            process.PriorityClass = priority switch
            {
                GameProcessPriority.Low => ProcessPriorityClass.Idle,
                GameProcessPriority.BelowNormal => ProcessPriorityClass.BelowNormal,
                GameProcessPriority.AboveNormal => ProcessPriorityClass.AboveNormal,
                GameProcessPriority.High => ProcessPriorityClass.High,
                _ => ProcessPriorityClass.Normal,
            };
        }
        catch (Exception)
        {
            // 高优先级需要额外权限，失败时保持默认。
        }
    }

    public static string Describe(LaunchPlan plan)
    {
        var builder = new StringBuilder();
        builder.Append(plan.JavaPath);

        foreach (var argument in plan.Arguments)
            builder.Append(' ').Append(argument);

        return builder.ToString();
    }
}
