using System.Runtime.InteropServices;

namespace NextCraftLauncher.Download.Java;

internal static class JavaRuntimePlatform
{
    public static string GetKey()
    {
        var architecture = RuntimeInformation.OSArchitecture;

        if (OperatingSystem.IsWindows())
        {
            return architecture switch
            {
                Architecture.Arm64 => "windows-arm64",
                Architecture.X86 => "windows-x86",
                _ => "windows-x64",
            };
        }

        if (OperatingSystem.IsMacOS())
            return architecture == Architecture.Arm64 ? "mac-os-arm64" : "mac-os";

        return architecture == Architecture.X86 ? "linux-i386" : "linux";
    }
}
