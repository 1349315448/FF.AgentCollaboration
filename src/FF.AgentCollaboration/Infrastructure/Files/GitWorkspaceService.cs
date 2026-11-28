using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Processes;

namespace FF.AgentCollaboration.Infrastructure.Files;

internal sealed record GitChange(string Path, string Group, string Code, int? Added, int? Deleted);
internal sealed record GitStatus(bool Available, bool Repository, string? Branch = null, string? Upstream = null,
    int Ahead = 0, int Behind = 0, IReadOnlyList<GitChange>? Changes = null, string? Hint = null);
internal sealed record GitDiff(string Text, string Revision, bool Truncated);
internal sealed record GitCommit(string Id, string Author, string Date, string Subject);
internal sealed record GitAction(string Project, string? Path, string? Message, string? ExpectedRevision,
    string? ExpectedBranch, string? ExpectedUpstream);

internal sealed class GitWorkspaceService(string project)
{
    private const int OutputLimit = 2 * 1024 * 1024;
    private static readonly SemaphoreSlim Mutation = new(1, 1);
    private readonly string metadata = Path.Combine(project, ".git");
    private readonly ProjectFileBrowser browser = new(project);

    private CancellationTokenSource Deadline(CancellationToken ct)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return timeout;
    }

    private string? Executable => HostCommand.Git;
    private bool IsRoot => Directory.Exists(metadata);

    private async Task Prepare(CancellationToken ct)
    {
        TopicArchive.CheckPath(metadata);
        TopicArchive.CheckPath(Path.Combine(metadata, "config"));
        // Git status can invoke a configured clean filter even though the workbench only reads status.
        var filters = await Read(["config", "--local", "--includes", "--null", "--get-regexp", @"^filter\..*\.(clean|smudge|process)$"], ct, acceptOne:true);
        if (filters.Split('\0').Any(record =>
        {
            int separator = record.IndexOf('\n');
            return separator >= 0 && separator < record.Length - 1;
        }))
            throw new WorkbenchError("此仓库配置了 Git 外部过滤器，请在原生终端操作", WorkbenchFailure.Unavailable);
    }

    private async Task<(int Code, string Output, bool Truncated, string Error)> Run(string[] arguments, CancellationToken ct)
    {
        string git = Executable ?? throw new WorkbenchError("未检测到 Git", WorkbenchFailure.Unavailable);
        var start = new ProcessStartInfo(git) {
            WorkingDirectory = project, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_NO_LAZY_FETCH"] = "1";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_ALLOW_PROTOCOL"] = "file:https:http:ssh";
        start.Environment["LC_ALL"] = "C";
        // Git 2.35.1 及更早版本把布尔值当成钩子程序名。空值在新旧版本中都关闭监视。
        foreach (string arg in new[] {"--no-pager", "--literal-pathspecs", "-c", "safe.directory=", "-c",
            "safe.directory=" + (OperatingSystem.IsWindows() ? project.Replace('\\','/') : project), "-c", "core.quotepath=false", "-c", "core.fsmonitor=",
            "-c", "core.hooksPath=/dev/null", "-c", "submodule.recurse=false", "-c", "maintenance.auto=false",
            "-c", "core.sshCommand=ssh -oBatchMode=yes", "-c", "core.askPass=", "--git-dir=" + metadata, "--work-tree=" + project}.Concat(arguments))
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new WorkbenchError("无法启动 Git", WorkbenchFailure.Unavailable);
        process.StandardInput.Close();
        try
        {
            var output = Drain(process.StandardOutput, OutputLimit, ct);
            var errors = Drain(process.StandardError, 3000, ct);
            await Task.WhenAll(output, errors, process.WaitForExitAsync(ct));
            return (process.ExitCode, output.Result.Text, output.Result.Truncated, errors.Result.Text);
        }
        catch (OperationCanceledException)
        {
            throw new WorkbenchError("Git 超过 10 秒或请求已取消，请刷新后检查仓库状态", WorkbenchFailure.Unavailable);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree:true); }
                catch (InvalidOperationException) { }
            }
        }
    }

    private static async Task<(string Text, bool Truncated)> Drain(StreamReader stream, int limit, CancellationToken ct)
    {
        var result = new StringBuilder();
        var buffer = new char[8192];
        bool truncated = false;
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            int kept = Math.Min(count, limit - result.Length);
            result.Append(buffer, 0, kept);
            truncated |= kept != count;
        }
        return (result.ToString(), truncated);
    }

    private async Task<string> Read(string[] args, CancellationToken ct, bool acceptOne = false)
    {
        var result = await Run(args, ct);
        if (result.Code != 0 && !(acceptOne && result.Code == 1))
            throw new WorkbenchError(GitError(result.Error), WorkbenchFailure.Conflict);
        if (result.Truncated) throw new WorkbenchError("Git 返回的内容过多，请在原生终端查看");
        return result.Output;
    }

    private static string GitError(string error)
    {
        string safe = Regex.Replace(error.Trim(), @"(?i)(https?://|ssh://)[^/\s@]+@", "$1***@");
        return string.IsNullOrWhiteSpace(safe) ? "Git 操作未完成，请刷新仓库状态" : safe;
    }

    private async Task<Dictionary<string, (int? Added, int? Deleted)>> Counts(bool staged, CancellationToken ct)
    {
        var args = new List<string> { "diff", "--numstat", "-z", "--no-renames", "--no-ext-diff", "--no-textconv", "--ignore-submodules=all" };
        if (staged) args.Add("--cached");
        var stats = await Read(args.ToArray(), ct);
        var result = new Dictionary<string, (int?, int?)>(StringComparer.Ordinal);
        foreach (string record in stats.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.Split('\t', 3);
            if (fields.Length == 3)
                result[fields[2]] = (int.TryParse(fields[0], out int add) ? add : null, int.TryParse(fields[1], out int del) ? del : null);
        }
        return result;
    }

    internal async Task<string[]?> DirtyEntries(CancellationToken ct)
    {
        if (!IsRoot)
        {
            for(var current=new DirectoryInfo(project);current!=null;current=current.Parent)
                if(Path.Exists(Path.Combine(current.FullName,".git")))
                {
                    if(Directory.Exists(Path.Combine(current.FullName,".git")))
                        return await new GitWorkspaceService(current.FullName).DirtyEntries(ct);
                    throw new WorkbenchError("此 Git 工作树需人工核对变更");
                }
            return null;
        }
        using var deadline=Deadline(ct);
        await Prepare(deadline.Token);
        var text=await Read(["status","--porcelain=v1","-z","--no-renames","--untracked-files=all","--ignore-submodules=all"],deadline.Token);
        return text.Split('\0',StringSplitOptions.RemoveEmptyEntries);
    }

    internal async Task<GitStatus> Status(CancellationToken ct)
    {
        if (Executable == null) return new(false, false, Hint:"未检测到 Git");
        if (!IsRoot) return new(true, false, Hint:"请选择包含 .git 目录的仓库根目录");
        using var timeout = Deadline(ct);
        ct = timeout.Token;
        await Prepare(ct);
        string response = await Read(["status", "--porcelain=v2", "-z", "--branch", "--no-renames",
            "--untracked-files=all", "--ignore-submodules=all"], ct);
        var index = await Counts(true, ct);
        var working = await Counts(false, ct);
        string? branch = null, upstream = null;
        int ahead = 0, behind = 0;
        var changes = new List<GitChange>();
        foreach (string record in response.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (record.StartsWith("# branch.head ")) branch = record[14..];
            else if (record.StartsWith("# branch.upstream ")) upstream = record[18..];
            else if (record.StartsWith("# branch.ab "))
            {
                var relative = record[12..].Split(' ');
                if (relative.Length == 2)
                {
                    int.TryParse(relative[0], out ahead);
                    int.TryParse(relative[1], out behind);
                    behind = Math.Abs(behind);
                }
            }
            else if (record.StartsWith("? ")) changes.Add(new(record[2..], "untracked", "?", null, null));
            else if (record.StartsWith("u "))
            {
                var conflict = record.Split(' ', 11);
                changes.Add(new(conflict[10], "conflict", conflict[1], null, null));
            }
            else if (record.StartsWith("1 "))
            {
                var tracked = record.Split(' ', 9);
                string path = tracked[8];
                if (tracked[1][0] != '.')
                {
                    var stat = index.GetValueOrDefault(path);
                    changes.Add(new(path, "staged", tracked[1][0].ToString(), stat.Added, stat.Deleted));
                }
                if (tracked[1][1] != '.')
                {
                    var stat = working.GetValueOrDefault(path);
                    changes.Add(new(path, "unstaged", tracked[1][1].ToString(), stat.Added, stat.Deleted));
                }
            }
        }
        return new(true, true, branch, upstream, ahead, behind, changes);
    }

    private void RequireRoot()
    {
        if (!IsRoot) throw new WorkbenchError("请选择包含 .git 目录的仓库根目录", WorkbenchFailure.NotFound);
    }

    // Git 只接收同一项目内的单个字面路径。目录输入不能变成递归暂存或递归放弃。
    private (string Path, WorkspaceDirectoryGuard Guard) FilePath(string? path)
    {
        string file = browser.Resolve(path);
        string relative = Path.GetRelativePath(project, file).Replace('\\','/');
        if (relative.Split('/').Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw new WorkbenchError("不能操作 Git 元数据", WorkbenchFailure.Forbidden);
        var parent = Path.GetDirectoryName(file)!;
        while (!Directory.Exists(parent) && !ProjectFolders.Same(parent, project))
            parent = Path.GetDirectoryName(parent)!;
        var guard = WorkspaceDirectoryGuard.Open(parent);
        try
        {
            TopicArchive.CheckPath(file);
            if (Directory.Exists(file)) throw new WorkbenchError("请选择单个文件");
            return (relative, guard);
        }
        catch { guard.Dispose(); throw; }
    }

    internal async Task<GitDiff> Diff(string path, bool staged, CancellationToken ct)
    {
        RequireRoot();
        using var timeout = Deadline(ct);
        ct = timeout.Token;
        await Prepare(ct);
        var file = FilePath(path);
        using var guard = file.Guard;
        return await ReadDiff(file.Path, staged, ct);
    }

    private async Task<GitDiff> ReadDiff(string path, bool staged, CancellationToken ct)
    {
        var options = new[] {"--no-color", "--no-ext-diff", "--no-textconv", "--no-renames", "--full-index", "--ignore-submodules=all"};
        string[] args = ["diff", .. options, .. (staged ? new[] {"--cached"} : Array.Empty<string>()), "--", path];
        var result = await Run(args, ct);
        if (result.Code != 0) throw new WorkbenchError(GitError(result.Error), WorkbenchFailure.Conflict);
        if (!staged && result.Output.Length == 0)
        {
            string untracked = await Read(["ls-files", "-z", "--others", "--exclude-standard", "--", path], ct);
            if (untracked.Length > 0)
            {
                result = await Run(["diff", "--no-index", .. options, "--", "/dev/null", path], ct);
                if (result.Code is not (0 or 1)) throw new WorkbenchError(GitError(result.Error), WorkbenchFailure.Conflict);
            }
        }
        // 包含对象 ID 的头部也参与核验，二进制变化同样无法沿用旧确认。
        return new(result.Output, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.Output))), result.Truncated);
    }

    internal async Task<IReadOnlyList<GitCommit>> Log(int limit, CancellationToken ct)
    {
        if (!IsRoot) return [];
        using var timeout = Deadline(ct);
        ct = timeout.Token;
        await Prepare(ct);
        if ((await Run(["rev-parse", "--verify", "HEAD"], ct)).Code != 0) return [];
        string output = await Read(["log", "-z", "-n", Math.Clamp(limit, 1, 100).ToString(), "--format=%h%x00%an%x00%aI%x00%s"], ct);
        var fields = output.Split('\0');
        var entries = new List<GitCommit>();
        for (int i = 0; i + 3 < fields.Length; i += 4)
            entries.Add(new(fields[i], fields[i+1], fields[i+2], fields[i+3]));
        return entries;
    }

    internal async Task Apply(string action, GitAction input, CancellationToken ct)
    {
        RequireRoot();
        using var timeout = Deadline(ct);
        ct = timeout.Token;
        if (!await Mutation.WaitAsync(TimeSpan.FromSeconds(1), ct))
            throw new WorkbenchError("另一个 Git 操作正在进行，请稍后重试", WorkbenchFailure.Conflict);
        try
        {
            await Prepare(ct);
            if (action is "stage" or "unstage" or "discard")
            {
                var file = FilePath(input.Path);
                using var guard = file.Guard;
                await ApplyFile(action, file.Path, input.ExpectedRevision, ct);
            }
            else if (action == "commit")
            {
                if (input.ExpectedBranch != null) await CheckBranch(input.ExpectedBranch, ct);
                string? message = input.Message?.Trim();
                if (string.IsNullOrEmpty(message) || message.Length > 4096 || message.Contains('\0'))
                    throw new WorkbenchError("请输入不超过 4096 字的提交说明");
                await Read(["-c", "commit.gpgsign=false", "commit", "--no-gpg-sign", "-m", message], ct);
            }
            else await SyncBranch(action, input, ct);
        }
        finally { Mutation.Release(); }
    }

    private async Task ApplyFile(string action, string path, string? revision, CancellationToken ct)
    {
        if (action == "stage") { await Read(["add", "--", path], ct); return; }
        if (action == "unstage")
        {
            string source = "HEAD";
            if ((await Run(["rev-parse", "--verify", source], ct)).Code != 0)
                source = (await Read(["hash-object", "-t", "tree", "--stdin"], ct)).Trim();
            await Read(["restore", "--staged", "--source=" + source, "--", path], ct);
            return;
        }
        if ((await Read(["ls-files", "-z", "--stage", "--", path], ct)).Length == 0)
            throw new WorkbenchError("未跟踪文件只能预览或暂存");
        if ((await Read(["ls-files", "-z", "--unmerged", "--", path], ct)).Length > 0)
            throw new WorkbenchError("请在终端解决合并冲突", WorkbenchFailure.Conflict);
        var current = await ReadDiff(path, false, ct);
        if (current.Truncated || current.Text.Length == 0 || current.Revision != revision)
            throw new WorkbenchError("磁盘上的改动已变化，请刷新并重新审核", WorkbenchFailure.Conflict);
        await Read(["restore", "--worktree", "--", path], ct);
    }

    private async Task<string> CheckBranch(string? expected, CancellationToken ct)
    {
        string branch = (await Read(["symbolic-ref", "--quiet", "--short", "HEAD"], ct)).Trim();
        if (branch != expected) throw new WorkbenchError("当前分支已变化，请刷新后重新确认", WorkbenchFailure.Conflict);
        return branch;
    }

    private async Task SyncBranch(string action, GitAction input, CancellationToken ct)
    {
        string branch = await CheckBranch(input.ExpectedBranch, ct);
        string remote = (await Read(["config", "--get", "branch." + branch + ".remote"], ct, acceptOne:true)).Trim();
        string upstream = (await Read(["config", "--get", "branch." + branch + ".merge"], ct, acceptOne:true)).Trim();
        if (remote.Length == 0 || remote.StartsWith('-') || remote == "." || !upstream.StartsWith("refs/heads/", StringComparison.Ordinal))
            throw new WorkbenchError("当前分支尚未绑定远端上游，请先在原生终端配置", WorkbenchFailure.Conflict);
        string tracking = (await Read(["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"], ct)).Trim();
        if (tracking != input.ExpectedUpstream)
            throw new WorkbenchError("远端上游已变化，请刷新后重新确认", WorkbenchFailure.Conflict);
        // 仓库配置中的远端辅助命令不能替工作台接管本机进程。账号沿用用户或系统配置。
        string repoConfig = await Read(["config", "--local", "--includes", "--name-only", "--get-regexp",
            @"^(credential(\..*)?\.helper|remote\..*\.(vcs|uploadpack|receivepack))$"], ct, acceptOne:true);
        if (repoConfig.Length != 0)
            throw new WorkbenchError("此仓库设置了自定义远端辅助程序，请在原生终端同步", WorkbenchFailure.Conflict);
        if (action == "pull")
            await Read(["pull", "--ff-only", "--no-rebase", "--no-autostash", "--no-recurse-submodules", "--", remote, upstream], ct);
        else if (action == "push")
            await Read(["push", "--no-follow-tags", "--no-mirror", "--recurse-submodules=no", "--", remote, "refs/heads/" + branch + ":" + upstream], ct);
    }
}
