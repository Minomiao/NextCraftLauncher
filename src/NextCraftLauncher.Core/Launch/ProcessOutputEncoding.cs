using System;
using System.Runtime.InteropServices;
using System.Text;

namespace NextCraftLauncher.Core.Launch;

/// <summary>
/// 解码 Java 子进程输出的编码。
/// <para>
/// JDK 19 起可以用 <c>-Dstdout.encoding=UTF-8</c> / <c>-Dstderr.encoding=UTF-8</c> 强制输出 UTF-8；
/// 更老的版本忽略这两个属性，在 Windows 上写的是<b>系统 ANSI 代码页</b>（中文系统为 936），
/// 所以必须按它解码，否则中文会变成乱码。
/// </para>
/// </summary>
public static class ProcessOutputEncoding
{
    private static readonly Lazy<Encoding> Ansi = new(ResolveAnsi);

    /// <summary>按 Java 大版本选择输出编码。</summary>
    public static Encoding For(int javaMajor)
    {
        if (javaMajor >= 19) return Encoding.UTF8;

        // 非 Windows 上 Java 一直用 UTF-8。
        return OperatingSystem.IsWindows() ? Ansi.Value : Encoding.UTF8;
    }

    private static Encoding ResolveAnsi()
    {
        try
        {
            var codePage = GetACP();
            if (codePage <= 0) return Encoding.UTF8;

            // 936 之类的代码页需要先注册 provider 才能取到。
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(codePage);
        }
        catch (Exception)
        {
            // 取不到代码页时退回 UTF-8：至少较新的 Java 是对的。
            return Encoding.UTF8;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern int GetACP();
}
