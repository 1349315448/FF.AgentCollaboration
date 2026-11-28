using FF.AgentCollaboration.Infrastructure.Processes;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Engines;

internal sealed record EngineAccount(string Status)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }
    internal bool Ready => Status is "authenticated" or "configured";
}

// 只探测账号，不创建会话。只向工作台返回状态，原生输出和凭据留在适配器内。
internal static class EngineAuthentication
{
    internal static async Task<EngineAccount> Check(string engine, string project, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(engine == "opencode" ? 45 : 15));
        try
        {
            using var adapter = Engine.Create(engine, project);
            return await adapter.Account(timeout.Token);
        }
        catch (WorkbenchError error) when (error.Failure == WorkbenchFailure.Unavailable)
        {
            return new("unavailable") { Message = error.Message };
        }
        catch (Exception error) when (!ct.IsCancellationRequested && error is not OutOfMemoryException)
        {
            return new("unknown");
        }
    }

    internal static async Task Require(string engine, string project, CancellationToken ct)
    {
        var account = await Check(engine, project, ct);
        if (account.Status == "unavailable") throw new WorkbenchError(account.Message!, WorkbenchFailure.Unavailable);
        if (!account.Ready) throw new WorkbenchError(account.Status == "signed-out" ? "请先登录，再选择新建或恢复会话" :
            "暂时无法确认登录状态，请重新检测", WorkbenchFailure.LoginRequired);
    }

    internal static Task<(int Code, string Output)> Command(string engine, string project, string[] args, CancellationToken ct) =>
        RunCommand(EngineExecutable.FindExecutable(engine), project, args, ct);

    internal static async Task<(int Code, string Output)> RunCommand(string executable, string project, string[] args, CancellationToken ct)
    {
        var start = HostCommand.Resolve(executable, args);
        start.WorkingDirectory = project;
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        using var process = Process.Start(start)!;
        using var onCancel = ct.Register(() => {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        process.StandardInput.Close();
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var errors = process.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(output, errors, process.WaitForExitAsync(ct));
            var stdout = (await output).Trim();
            return (process.ExitCode, stdout.Length > 0 ? stdout : (await errors).Trim());
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    internal static JsonNode? Settings(string path) => File.Exists(path) ?
        JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) : null;

    internal static bool HasKey(JsonNode? settings, string? key) => !string.IsNullOrWhiteSpace(key) &&
        (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)) || !string.IsNullOrWhiteSpace((string?)settings?["env"]?[key]));
}
