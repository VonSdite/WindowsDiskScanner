namespace WindowsDiskScanner.App;

public sealed class ScanNode
{
    internal ScanNode(
        string name,
        string fullPath,
        bool isDirectory,
        long sizeBytes,
        DateTime lastWriteTime)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        SizeBytes = sizeBytes;
        LastWriteTime = lastWriteTime;
        Children = isDirectory ? [] : null;
    }

    public string Name { get; }

    public string FullPath { get; }

    public bool IsDirectory { get; }

    public long SizeBytes { get; internal set; }

    public DateTime LastWriteTime { get; }

    public double PercentOfRoot { get; internal set; }

    public bool IsAccessible { get; internal set; } = true;

    public List<ScanNode>? Children { get; }
}
