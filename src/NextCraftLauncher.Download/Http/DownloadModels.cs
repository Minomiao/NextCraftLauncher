namespace NextCraftLauncher.Download;

public sealed record DownloadProgress(string FileName, long BytesReceived, long? TotalBytes)
{
    public double? Percent => TotalBytes is > 0
        ? (double)BytesReceived / TotalBytes.Value * 100
        : null;
}

public sealed record DownloadResult(string FilePath, long BytesWritten, bool Skipped, bool HashVerified);
