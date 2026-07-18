using System.IO;

namespace WindowsDiskScanner.App;

public sealed class ScanNode
{
    private readonly string _path;

    internal ScanNode(
        string name,
        string path,
        bool isDirectory,
        bool isLink,
        long sizeBytes,
        DateTime lastWriteTime)
    {
        Name = name;
        _path = path;
        IsDirectory = isDirectory;
        IsLink = isLink;
        SizeBytes = sizeBytes;
        LastWriteTime = lastWriteTime;
    }

    public string Name { get; }

    public string FullPath => IsDirectory ? _path : Path.Combine(_path, Name);

    public bool IsDirectory { get; }

    public bool IsLink { get; }

    public long SizeBytes { get; internal set; }

    public DateTime LastWriteTime { get; }

    public bool IsAccessible { get; internal set; } = true;

    public List<ScanNode>? Children { get; private set; }

    internal void AddChild(ScanNode child) =>
        (Children ??= []).Add(child);
}
