namespace WindowsDiskScanner.App;

public sealed record ScanResult(
    ScanNode Root,
    long DirectoryCount,
    long FileCount,
    long InaccessibleDirectoryCount,
    TimeSpan Elapsed);
