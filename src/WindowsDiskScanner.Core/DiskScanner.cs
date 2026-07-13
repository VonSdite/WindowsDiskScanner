using System.Diagnostics;
using System.IO.Enumeration;
using System.Threading.Channels;

namespace WindowsDiskScanner.Core;

public sealed class DiskScanner
{
    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false
    };

    private readonly int _workerCount;

    public DiskScanner(int? workerCount = null)
    {
        _workerCount = workerCount ?? Math.Clamp(Environment.ProcessorCount, 2, 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(_workerCount, 1);
    }

    public async Task<ScanResult> ScanAsync(
        string rootPath,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        string normalizedPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(normalizedPath))
        {
            throw new DirectoryNotFoundException($"目录不存在：{normalizedPath}");
        }

        cancellationToken.ThrowIfCancellationRequested();

        DirectoryInfo rootInfo = new(normalizedPath);
        ScanNode root = new(
            GetDisplayName(rootInfo),
            normalizedPath,
            isDirectory: true,
            sizeBytes: 0,
            rootInfo.LastWriteTime);

        Channel<ScanNode> directories = Channel.CreateUnbounded<ScanNode>(
            new UnboundedChannelOptions
            {
                AllowSynchronousContinuations = false,
                SingleReader = false,
                SingleWriter = false
            });

        long pendingDirectories = 1;
        long directoryCount = 1;
        long fileCount = 0;
        long inaccessibleDirectoryCount = 0;
        long discoveredBytes = 0;
        long lastProgressTimestamp = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();

        directories.Writer.TryWrite(root);
        ReportProgress(root.FullPath, force: true);

        Task[] workers = Enumerable.Range(0, _workerCount)
            .Select(_ => ScanWorkerAsync())
            .ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        AggregateDirectorySizes(root);
        SortTree(root);
        SetPercentages(root);
        stopwatch.Stop();
        ReportProgress(root.FullPath, force: true);

        return new ScanResult(
            root,
            directoryCount,
            fileCount,
            inaccessibleDirectoryCount,
            stopwatch.Elapsed);

        async Task ScanWorkerAsync()
        {
            await foreach (ScanNode directory in directories.Reader.ReadAllAsync())
            {
                try
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        ScanOneDirectory(directory);
                    }
                }
                finally
                {
                    if (Interlocked.Decrement(ref pendingDirectories) == 0)
                    {
                        directories.Writer.TryComplete();
                    }
                }
            }
        }

        void ScanOneDirectory(ScanNode directory)
        {
            ReportProgress(directory.FullPath, force: false);

            try
            {
                foreach (FileEntry entry in Enumerate(directory.FullPath))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    if (entry.IsDirectory)
                    {
                        ScanNode childDirectory = new(
                            entry.Name,
                            entry.FullPath,
                            isDirectory: true,
                            sizeBytes: 0,
                            entry.LastWriteTime);

                        directory.Children!.Add(childDirectory);
                        Interlocked.Increment(ref directoryCount);
                        Interlocked.Increment(ref pendingDirectories);

                        if (!directories.Writer.TryWrite(childDirectory))
                        {
                            Interlocked.Decrement(ref pendingDirectories);
                        }
                    }
                    else
                    {
                        ScanNode file = new(
                            entry.Name,
                            entry.FullPath,
                            isDirectory: false,
                            entry.Length,
                            entry.LastWriteTime);

                        directory.Children!.Add(file);
                        Interlocked.Increment(ref fileCount);
                        Interlocked.Add(ref discoveredBytes, entry.Length);
                    }
                }
            }
            catch (Exception exception) when (IsExpectedFileSystemException(exception))
            {
                directory.IsAccessible = false;
                Interlocked.Increment(ref inaccessibleDirectoryCount);
            }
        }

        void ReportProgress(string currentPath, bool force)
        {
            if (progress is null)
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            long previous = Interlocked.Read(ref lastProgressTimestamp);
            double elapsedSinceLastReport = (now - previous) / (double)Stopwatch.Frequency;
            if (!force && elapsedSinceLastReport < 0.12)
            {
                return;
            }

            if (!force && Interlocked.CompareExchange(ref lastProgressTimestamp, now, previous) != previous)
            {
                return;
            }

            if (force)
            {
                Interlocked.Exchange(ref lastProgressTimestamp, now);
            }

            progress.Report(new ScanProgress(
                currentPath,
                Interlocked.Read(ref directoryCount),
                Interlocked.Read(ref fileCount),
                Interlocked.Read(ref discoveredBytes),
                stopwatch.Elapsed));
        }
    }

    private static IEnumerable<FileEntry> Enumerate(string directoryPath)
    {
        FileSystemEnumerable<FileEntry> entries = new(
            directoryPath,
            static (ref FileSystemEntry entry) => new FileEntry(
                entry.FileName.ToString(),
                entry.ToFullPath(),
                entry.IsDirectory,
                entry.IsDirectory ? 0 : entry.Length,
                entry.LastWriteTimeUtc.LocalDateTime),
            EnumerationOptions);

        entries.ShouldIncludePredicate = static (ref FileSystemEntry entry) =>
            (entry.Attributes & FileAttributes.ReparsePoint) == 0;

        return entries;
    }

    private static void AggregateDirectorySizes(ScanNode root)
    {
        Stack<(ScanNode Node, bool Visited)> stack = new();
        stack.Push((root, false));

        while (stack.TryPop(out (ScanNode Node, bool Visited) item))
        {
            if (!item.Node.IsDirectory)
            {
                continue;
            }

            if (!item.Visited)
            {
                stack.Push((item.Node, true));
                foreach (ScanNode child in item.Node.Children!)
                {
                    if (child.IsDirectory)
                    {
                        stack.Push((child, false));
                    }
                }

                continue;
            }

            long total = 0;
            foreach (ScanNode child in item.Node.Children!)
            {
                total = SaturatingAdd(total, child.SizeBytes);
            }

            item.Node.SizeBytes = total;
        }
    }

    private static void SortTree(ScanNode root)
    {
        Stack<ScanNode> stack = new();
        stack.Push(root);

        while (stack.TryPop(out ScanNode? directory))
        {
            directory.Children!.Sort(CompareNodes);
            foreach (ScanNode child in directory.Children)
            {
                if (child.IsDirectory)
                {
                    stack.Push(child);
                }
            }
        }
    }

    private static void SetPercentages(ScanNode root)
    {
        double totalBytes = root.SizeBytes;
        Stack<ScanNode> stack = new();
        stack.Push(root);

        while (stack.TryPop(out ScanNode? node))
        {
            node.PercentOfRoot = totalBytes == 0
                ? 0
                : node.SizeBytes / totalBytes * 100;

            if (node.Children is null)
            {
                continue;
            }

            foreach (ScanNode child in node.Children)
            {
                stack.Push(child);
            }
        }
    }

    private static int CompareNodes(ScanNode left, ScanNode right)
    {
        if (left.IsDirectory != right.IsDirectory)
        {
            return left.IsDirectory ? -1 : 1;
        }

        int sizeComparison = right.SizeBytes.CompareTo(left.SizeBytes);
        return sizeComparison != 0
            ? sizeComparison
            : StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
    }

    private static bool IsExpectedFileSystemException(Exception exception) =>
        exception is UnauthorizedAccessException
            or IOException
            or System.Security.SecurityException;

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private static string GetDisplayName(DirectoryInfo directoryInfo) =>
        string.IsNullOrEmpty(directoryInfo.Name)
            ? directoryInfo.FullName
            : directoryInfo.Name;

    private readonly record struct FileEntry(
        string Name,
        string FullPath,
        bool IsDirectory,
        long Length,
        DateTime LastWriteTime);
}
