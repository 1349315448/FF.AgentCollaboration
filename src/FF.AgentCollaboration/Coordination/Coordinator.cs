using System.Text;
using System.Text.Json;
using FF.AgentCollaboration.Engines;

namespace FF.AgentCollaboration.Coordination;

internal static class Coordinator
{
    internal static async Task<int> Run(string root, string[] args)
    {
        bool create = args[0] is "run" or "init";
        if (create && Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException("新验收需要空目录。恢复已有运行请使用 resume 或 recover。");
        if (create) Directory.CreateDirectory(root);
        // 限定一个调度写者，已认领的事件由恢复流程处理。
        using var owner = new FileStream(Path.Combine(root, "coordinator.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var state = new State(root, create);
        if (args[0] == "init") return 0;
        int modelOption = Array.IndexOf(args, "--copilot-model");
        string? model = modelOption < 0 ? null : args[modelOption + 1];
        bool disconnect = args.Contains("--disconnect-on-callback");
        var faultPath = Path.Combine(root, "disconnect.json");
        if (disconnect && !File.Exists(faultPath))
            File.WriteAllText(faultPath, JsonSerializer.Serialize(new FaultEvidence(null, null)));
        bool faultArmed = File.Exists(faultPath) && JsonSerializer.Deserialize<FaultEvidence>(File.ReadAllText(faultPath))?.DisconnectedAt == null;
        try { return await Drive(state, args[0] == "recover", model, faultArmed, faultPath); }
        catch (LostCallback lost)
        {
            // Drive 的 finally 已关闭引擎，再保存断线证据。恢复命令仍须独立核对原生历史。
            File.WriteAllText(faultPath, JsonSerializer.Serialize(new FaultEvidence(lost.EventId, DateTimeOffset.UtcNow)));
            Console.Error.WriteLine($"DISCONNECTED: {lost.EventId} committed its tool result; connection closed before acknowledgement.");
            return 75;
        }
    }

    static string Prompt(State state, Dispatch e)
    {
        var job = state.TaskFor(e.TaskId);
        string actor = state.WindowFor(e.Target).Marker;
        return e.Phase switch
        {
            "open" => $"Your private session marker is {actor}. Remember it for future turns. Invoke the MCP tool named handoff_task on the lx_collab server (search your tools for handoff_task if needed). Use taskId {job.Id} and a short document in text instructing {job.Worker} to return the document code {job.Challenge} and their OWN previously remembered private marker. Include the document code literally. IMPORTANT: do not include your own PRIVATE marker in the handoff. Then stop.",
            "work" => $"Your peer assigned task {job.Id}. First call lx_collab read_handoff with taskId {job.Id}. Read that document, then call lx_collab report_task_done with taskId {job.Id} and text containing its exact document code and your own private marker from your earlier turn. Then stop.",
            "callback" => $"Task {job.Id} was completed by {job.Worker}. Delivery: {job.Result}\nUse your OWN private marker from the earlier turn and the worker's result above. Call lx_collab verify_result with taskId {job.Id} and text containing the document code and BOTH exact PRIVATE markers. Then stop.",
            _ => throw new InvalidOperationException("未知投递阶段")
        };
    }

    static async Task<int> Drive(State state, bool recoverOnly, string? model, bool disconnect, string faultPath)
    {
        var engines = new Dictionary<string, (RpcEngine Engine, string Session)>();
        try
        {
            // [04] 恢复只观察。没有该事件的工具回执时保留待核对，不通过再次发送来探测。
            foreach (var e in state.Events().Where(x => x.State is "in_flight" or "delivery_uncertain"))
            {
                var proof = State.Effect(state.TaskFor(e.TaskId), e.Phase);
                if (proof == null) { state.Finish(e.Id, "delivery_uncertain"); continue; }
                if (e.Target == "codex")
                {
                    state.Finish(e.Id, "delivery_uncertain");
                    int matches;
                    try
                    {
                        using var reader = new CodexEngine(DemoDirectory(state, "codex"));
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                        await reader.Start(null, deadline.Token);
                        matches = await reader.CountCodexInputs(state.WindowFor("codex").Session!, e.Id, deadline.Token);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"原生历史暂不可核对，保留 {e.Id} 待核对：{ex.Message}");
                        continue;
                    }
                    state.HistoryCount(e.Id, matches);
                    Console.WriteLine($"reconcile {e.Id} native user inputs={matches}");
                    if (matches != 1) { state.Finish(e.Id, "delivery_uncertain"); continue; }
                }
                state.Finish(e.Id, "recovered");
            }
            if (!recoverOnly && !state.Events().Any(x => x.State == "delivery_uncertain"))
            {
                while (state.Events().FirstOrDefault(x => x.State == "pending") is { } e)
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
                    if (!engines.TryGetValue(e.Target, out var window))
                    {
                        var engine = e.Target == "codex" ? (RpcEngine)new CodexEngine(DemoDirectory(state, e.Target))
                            : new CopilotEngine(DemoDirectory(state, e.Target));
                        try
                        {
                            var saved = state.WindowFor(e.Target);
                            var grant = state.IssueGrant(e.Target);
                            var mcp = new EngineMcp(state.Root, "mcp", grant, CoordinatorMcp.P0ToolNames);
                            var native = await engine.Open(saved.Session, e.Target == "copilot" ? model ?? saved.Model : null,
                                mcp, "This is a controlled two-agent handoff test. Use only lx_collab tools, exactly as requested. Do not run shell commands or access unrelated files. Keep your PRIVATE marker inside your own session until asked for a result.",
                                "read-only", "never", deadline.Token);
                            state.Bind(e.Target, native.Id, native.Model);
                            Console.WriteLine($"{e.Target} session={native.Id} model={native.Model}");
                            window = (engine, native.Id);
                            engines.Add(e.Target, window);
                        }
                        catch
                        {
                            engine.Dispose();
                            throw;
                        }
                    }
                    if (!state.Claim(e.Id)) continue;
                    Console.WriteLine($"dispatch {e.Phase} {e.TaskId} -> {e.Target} event={e.Id}");
                    var text = new StringBuilder();
                    bool breakLink = disconnect && e is { Phase: "callback", Target: "codex" };
                    await window.Engine.Turn(window.Session, e.Id, $"[TASK:{e.TaskId}] {Prompt(state, e)}",
                        turn => state.Started(e.Id, turn), delta => { if (text.Length < 1200) text.Append(delta); },
                        breakLink ? () =>
                        {
                            if (State.Effect(state.TaskFor(e.TaskId), e.Phase) == null) return;
                            window.Engine.Abort();
                            throw new LostCallback(e.Id);
                        }
                    : null, deadline.Token);
                    if (State.Effect(state.TaskFor(e.TaskId), e.Phase) == null)
                    {
                        string diagnostic = System.Text.RegularExpressions.Regex.Replace(text.ToString(),
                            @"[a-fA-F0-9]{64}|(?:github_pat_|ghp_|Bearer )[A-Za-z0-9_.-]+", "[redacted]");
                        throw new InvalidOperationException($"{e.Target} 结束但没有提交 {e.TaskId}/{e.Phase}。引擎回复：{diagnostic[..Math.Min(600, diagnostic.Length)]}");
                    }
                    state.Finish(e.Id, "completed");
                    state.Export();
                }
            }
            var events = state.Events();
            var protocol = state.ProtocolEvidence();
            bool success = events.Count == 6 && events.All(e => e.Attempts == 1 && e.State is "completed" or "recovered");
            success &= events.All(e => protocol.Any(p => p.EventId == e.Id && p.Started != null && (e.Target != "codex" || p.NativeTurn != null)));
            FaultEvidence? fault = null;
            if (File.Exists(faultPath))
            {
                fault = JsonSerializer.Deserialize<FaultEvidence>(File.ReadAllText(faultPath));
                success &= fault?.DisconnectedAt != null &&
                    events.Any(e => e.Id == fault.EventId && e is { Phase: "callback", Target: "codex", State: "recovered" }) &&
                    protocol.Any(p => p.EventId == fault.EventId && p.HistoryMatches == 1);
            }
            foreach (var id in new[] { "A-B", "B-A" })
            {
                var j = state.TaskFor(id);
                var origin = state.WindowFor(j.Origin).Marker;
                var worker = state.WindowFor(j.Worker).Marker;
                success &= j.Document?.Contains(j.Challenge) == true && !j.Document.Contains(origin) &&
                    j.Result?.Contains(j.Challenge) == true && j.Result.Contains(worker) &&
                    j.Verification?.Contains(origin) == true && j.Verification.Contains(worker) && j.Verification.Contains(j.Challenge);
            }
            var report = new { success, windows = new[] { state.WindowFor("codex"), state.WindowFor("copilot") }, events, protocol, fault };
            File.WriteAllText(Path.Combine(state.Root, "acceptance.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(success ? "PASS: both original sessions verified the documents and both private markers." : "INCOMPLETE: inspect acceptance.json and the native session evidence.");
            return success ? 0 : 2;
        }
        finally
        {
            foreach (var window in engines.Values) window.Engine.Dispose();
            state.Export();
        }
    }

    static string DemoDirectory(State state, string engine) =>
        Directory.CreateDirectory(Path.Combine(state.Root, "workspaces", engine)).FullName;
}

internal sealed record FaultEvidence(string? EventId, DateTimeOffset? DisconnectedAt);
internal sealed class LostCallback(string eventId) : Exception
{
    internal string EventId { get; } = eventId;
}
