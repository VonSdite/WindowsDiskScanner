using System.Text;

namespace WindowsDiskScanner.App;

public sealed record AiPrompt(string SystemPrompt, string UserPrompt);

public static class AiPromptBuilder
{
    public static AiPrompt BuildScanReport(ScanResult result)
    {
        const int maxEntries = 25;
        List<ScanNode> largestDirectories = [];
        List<ScanNode> largestFiles = [];
        Stack<ScanNode> pending = new();
        pending.Push(result.Root);
        while (pending.TryPop(out ScanNode? node))
        {
            if (node.IsDirectory)
            {
                if (!ReferenceEquals(node, result.Root))
                {
                    AddLargest(largestDirectories, node, maxEntries);
                }

                if (node.Children is { } children)
                {
                    foreach (ScanNode child in children)
                    {
                        pending.Push(child);
                    }
                }
            }
            else
            {
                AddLargest(largestFiles, node, maxEntries);
            }
        }

        StringBuilder prompt = new();
        prompt.AppendLine("请根据以下 Windows 目录扫描结果生成中文磁盘分析报告。");
        prompt.AppendLine($"扫描根目录：{result.Root.FullPath}");
        prompt.AppendLine($"总占用：{FormatBytes(result.Root.SizeBytes)}");
        prompt.AppendLine($"目录数：{result.DirectoryCount:N0}");
        prompt.AppendLine($"文件数：{result.FileCount:N0}");
        prompt.AppendLine();
        prompt.AppendLine("根目录下的主要项目：");
        foreach (ScanNode node in result.Root.Children?.Take(30) ?? [])
        {
            prompt.AppendLine(FormatNode(node, result.Root.SizeBytes));
        }

        prompt.AppendLine();
        prompt.AppendLine("占用最大的目录：");
        foreach (ScanNode node in largestDirectories)
        {
            prompt.AppendLine(FormatNode(node, result.Root.SizeBytes));
        }

        prompt.AppendLine();
        prompt.AppendLine("占用最大的文件：");
        foreach (ScanNode node in largestFiles)
        {
            prompt.AppendLine(FormatNode(node, result.Root.SizeBytes));
        }

        return new AiPrompt(
            "你是 Windows 磁盘清理分析助手。说明大目录和大文件可能属于什么软件、承担什么作用、是否通常可以删除。无法从路径可靠判断时明确写出不确定，不要把系统目录、程序安装目录或用户数据直接标记为可安全删除。报告使用清晰的小标题，并把建议分为“可考虑清理”“需要确认”“不建议删除”。",
            prompt.ToString());
    }

    public static AiPrompt BuildNodeExplanation(IReadOnlyList<TreeRow> rows)
    {
        StringBuilder prompt = new();
        prompt.AppendLine("请解释以下 Windows 文件或目录通常是什么软件产生的、用途是什么，以及删除风险。上下文可能包含多个项目，请逐项回答并在最后给出整体建议。");
        prompt.AppendLine();
        foreach (TreeRow row in rows.Take(50))
        {
            ScanNode node = row.Node;
            string type = (node.IsDirectory, node.IsLink) switch
            {
                (true, true) => "目录链接",
                (true, false) => "目录",
                (false, true) => "文件链接",
                _ => "文件"
            };
            prompt.AppendLine($"- 类型：{type}");
            prompt.AppendLine($"  路径：{node.FullPath}");
            prompt.AppendLine($"  大小：{FormatBytes(node.SizeBytes)}");
            if (node.IsDirectory && node.Children is { Count: > 0 } children)
            {
                prompt.AppendLine("  主要子项：");
                foreach (ScanNode child in children.Take(8))
                {
                    prompt.AppendLine($"    - {child.Name}（{FormatBytes(child.SizeBytes)}）");
                }
            }

            prompt.AppendLine();
        }

        return new AiPrompt(
            "你是 Windows 文件来源分析助手。根据路径、名称和目录结构判断软件来源与用途。不要臆造确定结论；不确定时说明可能性和可验证方法。删除建议必须区分缓存、日志、安装文件、程序组件、系统文件和用户数据，并优先保护用户数据。",
            prompt.ToString());
    }

    private static void AddLargest(List<ScanNode> nodes, ScanNode candidate, int capacity)
    {
        int index = nodes.FindIndex(node => candidate.SizeBytes > node.SizeBytes);
        if (index < 0)
        {
            if (nodes.Count < capacity)
            {
                nodes.Add(candidate);
            }
        }
        else
        {
            nodes.Insert(index, candidate);
            if (nodes.Count > capacity)
            {
                nodes.RemoveAt(nodes.Count - 1);
            }
        }
    }

    private static string FormatNode(ScanNode node, long rootSizeBytes)
    {
        double percent = rootSizeBytes == 0 ? 0 : node.SizeBytes / (double)rootSizeBytes * 100;
        return $"- {node.FullPath} | {FormatBytes(node.SizeBytes)} | {percent:N2}%";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes:N0} B" : $"{size:N2} {units[unit]}";
    }
}
