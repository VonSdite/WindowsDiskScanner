using WindowsDiskScanner.Core;

string testRoot = Path.Combine(Path.GetTempPath(), $"WindowsDiskScanner-{Guid.NewGuid():N}");

try
{
    Directory.CreateDirectory(Path.Combine(testRoot, "alpha", "nested"));
    Directory.CreateDirectory(Path.Combine(testRoot, "beta"));
    await File.WriteAllBytesAsync(Path.Combine(testRoot, "root.bin"), new byte[3]);
    await File.WriteAllBytesAsync(Path.Combine(testRoot, "alpha", "first.bin"), new byte[5]);
    await File.WriteAllBytesAsync(Path.Combine(testRoot, "alpha", "nested", "second.bin"), new byte[7]);
    await File.WriteAllBytesAsync(Path.Combine(testRoot, "beta", "third.bin"), new byte[11]);

    DiskScanner scanner = new(workerCount: 3);
    ScanResult result = await scanner.ScanAsync(testRoot);

    AssertEqual(26L, result.Root.SizeBytes, "根目录大小");
    AssertEqual(4L, result.DirectoryCount, "目录数量");
    AssertEqual(4L, result.FileCount, "文件数量");
    AssertEqual(0L, result.InaccessibleDirectoryCount, "不可访问目录数量");

    ScanNode[] rootChildren = result.Root.Children!.ToArray();
    AssertEqual("alpha", rootChildren[0].Name, "目录按大小降序排列");
    AssertEqual("beta", rootChildren[1].Name, "第二个目录");
    AssertEqual("root.bin", rootChildren[2].Name, "文件排列在目录之后");
    AssertEqual(12L, rootChildren[0].SizeBytes, "子目录递归汇总");
    AssertNear(100, result.Root.PercentOfRoot, "根目录占比");

    using CancellationTokenSource cancellation = new();
    cancellation.Cancel();
    await AssertCanceledAsync(() => scanner.ScanAsync(testRoot, cancellationToken: cancellation.Token));

    Console.WriteLine("验证通过：大小汇总、计数、排序、占比和取消行为均正确。");
    return 0;
}
finally
{
    if (Directory.Exists(testRoot))
    {
        Directory.Delete(testRoot, recursive: true);
    }
}

static void AssertEqual<T>(T expected, T actual, string name)
    where T : IEquatable<T>
{
    if (!expected.Equals(actual))
    {
        throw new InvalidOperationException($"{name}验证失败，期望 {expected}，实际 {actual}。");
    }
}

static void AssertNear(double expected, double actual, string name)
{
    if (Math.Abs(expected - actual) > 0.001)
    {
        throw new InvalidOperationException($"{name}验证失败，期望 {expected}，实际 {actual}。");
    }
}

static async Task AssertCanceledAsync(Func<Task> action)
{
    try
    {
        await action();
    }
    catch (OperationCanceledException)
    {
        return;
    }

    throw new InvalidOperationException("取消行为验证失败，扫描未抛出取消异常。");
}
