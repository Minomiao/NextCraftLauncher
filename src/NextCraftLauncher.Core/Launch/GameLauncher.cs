using System.Diagnostics;
using System.Text;

namespace NextCraftLauncher.Core.Launch;

public static class GameLauncher
{
    /// <summary>
    /// 启动 Java 进程，并把标准输出/错误逐行转发给日志回调。
    /// </summary>
    public static async Task<int> RunAsync(
        LaunchPlan plan,
        IProgress<string>? log = null,
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

    public static string Describe(LaunchPlan plan)
    {
        var builder = new StringBuilder();
        builder.Append(plan.JavaPath);

        foreach (var argument in plan.Arguments)
            builder.Append(' ').Append(argument);

        return builder.ToString();
    }
}
