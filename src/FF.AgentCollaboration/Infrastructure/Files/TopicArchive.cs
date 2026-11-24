using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Infrastructure.Files;

// [T12] 话题目录一经绑定保持稳定，改话题名不会移动已有文件。
internal static class TopicArchive
{
    internal static void CheckPath(string path)
    {
        // 归档目录可能由用户后续替换为 junction。写入或授权读取时重新检查路径。
        for (string? part = path; part != null; part = Path.GetDirectoryName(part))
        {
            try
            {
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new WorkbenchError("归档路径不能经过链接或重解析点", WorkbenchFailure.Forbidden);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static string Create(string archiveRoot, WorkbenchTopic topic, string? existing)
    {
        CheckPath(archiveRoot);
        Directory.CreateDirectory(archiveRoot);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            string folder = ProjectFolders.Resolve(existing);
            if (!ProjectFolders.Same(Path.GetDirectoryName(folder) ?? "", archiveRoot))
                throw new WorkbenchError("请选择当前归档根下的已有任务目录");
            Prepare(folder, topic);
            return folder;
        }

        string slug = Regex.Replace(topic.Name, @"[^\p{L}\p{Nd}]+", "-").Trim('-');
        var date = DateTime.TryParse(topic.CreatedAt, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var created)
            ? created.ToLocalTime() : DateTime.Today;
        string staging = Path.Combine(archiveRoot, ".creating-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            Prepare(staging, topic);
            return PublishNumberedFolder(archiveRoot, staging, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                slug.Length == 0 ? "topic" : slug);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                File.Delete(Path.Combine(staging, "PLAN.md"));
                string handoffs = Path.Combine(staging, "handoffs");
                if (Directory.Exists(handoffs)) Directory.Delete(handoffs);
                Directory.Delete(staging);
            }
        }
    }

    private static string PublishNumberedFolder(string root, string staging, string date, string slug)
    {
        // 独立工作台可共用归档目录。选号和发布需一起串行，才能保证不同话题名也不重号。
        string rootKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())));
        using var mutex = new Mutex(false, @"Global\LX.TopicArchive." + rootKey);
        try { mutex.WaitOne(); }
        catch (AbandonedMutexException) { /* 已接管锁，从实际目录重新计算序号。 */ }
        try
        {
            long maximum = 0;
            foreach (var folder in new DirectoryInfo(root).EnumerateDirectories(date + "-*"))
            {
                var match = Regex.Match(folder.Name[(date.Length + 1)..], @"^([0-9]{3,})-.");
                if (match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long number))
                    maximum = Math.Max(maximum, number);
            }
            string next = checked(maximum + 1).ToString("D3", CultureInfo.InvariantCulture);
            string destination = Path.Combine(root, $"{date}-{next}-{slug}");
            Directory.Move(staging, destination);
            return destination;
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static void Prepare(string folder, WorkbenchTopic topic)
    {
        string handoffs = Path.Combine(folder, "handoffs"), plan = Path.Combine(folder, "PLAN.md");
        CheckPath(handoffs);
        CheckPath(plan);
        Directory.CreateDirectory(handoffs);
        if (!File.Exists(plan))
            Publish(plan, $"# {topic.Name}\n\n项目：{topic.Project}\n\n## 目标\n\n{topic.Goal}\n", overwrite: false);
    }

    internal static void Publish(string path, string contents, bool overwrite)
    {
        CheckPath(path);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, contents, new UTF8Encoding(false));
            try { File.Move(temp, path, overwrite); }
            catch (IOException) when (!overwrite && File.Exists(path)) { }
        }
        finally { File.Delete(temp); }
    }
}
