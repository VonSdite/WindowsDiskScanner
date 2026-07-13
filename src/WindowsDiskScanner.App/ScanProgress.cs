namespace WindowsDiskScanner.App;

public sealed record ScanProgress(
    string CurrentPath,
    long DirectoryCount,
    long FileCount,
    long DiscoveredBytes,
    TimeSpan Elapsed);
