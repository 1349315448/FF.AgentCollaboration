using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Infrastructure.Windows;

namespace FF.AgentCollaboration.Infrastructure.Terminal;

internal sealed record TerminalFrame(long Revision, byte[] Data, int MentionRequest = 0);
internal sealed record TerminalViewer(int Id, int Columns, int Rows, TerminalFrame[] Replay, ChannelReader<TerminalFrame> Live);

// [01、04] ConPTY 输入与输出由独立管道处理。唯一写入锁同时保护键盘和外部消息。
internal sealed class TerminalSession : IDisposable
{
    private readonly object outputLock = new();
    private readonly object consoleLock = new();
    private readonly SemaphoreSlim inputLock = new(1);
    private readonly SemaphoreSlim deliveryLock = new(1);
    private readonly Queue<TerminalFrame> replay = new();
    private readonly TerminalReplay discarded = new();
    private readonly Dictionary<int, Channel<TerminalFrame>> viewers = new();
    private readonly Dictionary<int, int> mentionPickers = new(); // 正数是待确认的请求，0 表示该查看者已收到确认。
    private bool automaticDeliveryInProgress;
    private readonly Dictionary<int, long> ready = new();
    private readonly Dictionary<int, (long Revision, string Message, long ObservedAt)> visibleDrafts = new();
    private readonly FileStream input;
    private readonly FileStream output;
    private readonly SafeProcessHandle process;
    private readonly TerminalJob job;
    private readonly Task drain;
    private nint console;
    private long revision;
    private long nextPromptRevision;
    private long pasteRevision = -1;
    private long clearedDraftRevision = -1;
    private long clearedAtRevision = -1;
    private long keyboardVersion;
    private readonly long launchedAt = Stopwatch.GetTimestamp();
    private readonly Dictionary<int, (long Revision, string Screen, string? Draft, bool Idle)> screens = new();
    private long? awaitingScreenSince;
    private bool bracketedPasteEnabled;
    private bool draftMayContainText;
    private long lastActivity = Stopwatch.GetTimestamp();
    private int replayBytes;
    private int nextViewer;
    private int disposed;
    private int columns;
    private int lines;
    private uint? exitCode;
    private bool ended;
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(1500);
    private readonly Action changed;
    private readonly Timer? quietTimer;
    private (bool Online, bool InputReady, string Activity)? lastReported;

    internal TerminalSession(string executable, IReadOnlyList<string> arguments, string cwd, string grant, int cols, int rows, Action changed,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        this.changed = changed;
        columns = cols;
        lines = rows;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            throw new WorkbenchError("真终端需要支持 ConPTY 的 Windows");
        if (!Native.CreatePipe(out var consoleInput, out var hostInput, 0, 0))
            throw Native.Error("无法创建终端输入管道");
        using var inputSide = consoleInput;
        if (!Native.CreatePipe(out var hostOutput, out var consoleOutput, 0, 0))
        {
            hostInput.Dispose();
            throw Native.Error("无法创建终端输出管道");
        }
        using var outputSide = consoleOutput;
        try
        {
            job = new TerminalJob();
            Marshal.ThrowExceptionForHR(Native.CreatePseudoConsole(new((short)cols, (short)rows), consoleInput, consoleOutput, 0, out console));
            input = new FileStream(hostInput, FileAccess.Write, bufferSize: 1);
            output = new FileStream(hostOutput, FileAccess.Read, bufferSize: 4096);
            drain = Task.Factory.StartNew(Drain, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            process = Launch(executable, arguments, cwd, grant, environment);
            lock (outputLock) quietTimer = new Timer(_ => QuietElapsed(), null, Quiet, Timeout.InfiniteTimeSpan);
        }
        catch
        {
            job?.Dispose();
            inputSide.Dispose();
            outputSide.Dispose();
            if (console != 0) Native.ClosePseudoConsole(console);
            input?.Dispose();
            drain?.GetAwaiter().GetResult();
            output?.Dispose();
            hostInput.Dispose();
            hostOutput.Dispose();
            throw;
        }
    }

    internal string Generation { get; } = Guid.NewGuid().ToString("N");
    internal uint? ExitCode
    {
        get
        {
            lock (outputLock)
                return disposed == 0 ? ReadExitCode() : exitCode;
        }
    }

    private uint? ReadExitCode() =>
        Native.WaitForSingleObject(process, 0) == 0 && Native.GetExitCodeProcess(process, out uint code) ? code : null;
    internal bool Online
    {
        get
        {
            lock (outputLock) return !ended && Volatile.Read(ref disposed) == 0 && Native.WaitForSingleObject(process, 0) == 258;
        }
    }

    private void Drain()
    {
        var buffer = new byte[8192];
        try
        {
            int size;
            while ((size = output.Read(buffer)) > 0)
            {
                lock (outputLock)
                {
                    var frame = new TerminalFrame(++revision, buffer.AsSpan(0, size).ToArray());
                    Touch();
                    awaitingScreenSince ??= lastActivity;
                    ready.Clear();
                    replay.Enqueue(frame);
                    replayBytes += size;
                    while (replayBytes > 524_288)
                    {
                        var removed = replay.Dequeue();
                        discarded.RemovePrefix(removed.Data);
                        replayBytes -= removed.Data.Length;
                    }
                    foreach (var (id, viewer) in viewers.ToArray())
                    {
                        if (viewer.Writer.TryWrite(frame)) continue;
                        viewer.Writer.TryComplete(new IOException("终端连接读取过慢"));
                        viewers.Remove(id);
                        mentionPickers.Remove(id);
                    }
                }
            }
        }
        catch (IOException) { }
        finally
        {
            lock (outputLock)
            {
                ended = true;
                foreach (var viewer in viewers.Values) viewer.Writer.TryComplete();
            }
            changed();
        }
    }

    internal TerminalViewer Subscribe()
    {
        lock (consoleLock)
            lock (outputLock)
            {
                var channel = Channel.CreateBounded<TerminalFrame>(256);
                int id = ++nextViewer;
                if (ended) channel.Writer.TryComplete();
                else viewers.Add(id, channel);
                var retained = replay.ToArray();
                var modes = discarded.RestoreModes();
                if (modes.Length != 0 && retained.Length != 0)
                    retained = [new TerminalFrame(retained[0].Revision - 1, modes), .. retained];
                return new TerminalViewer(id, columns, lines, retained, channel.Reader);
            }
    }

    internal void Detach(int id)
    {
        lock (outputLock)
        {
            ready.Remove(id);
            visibleDrafts.Remove(id);
            screens.Remove(id);
            mentionPickers.Remove(id);
            if (viewers.Remove(id, out var channel)) channel.Writer.TryComplete();
            NotifyView();
        }
    }

    internal void ObservePrompt(int viewerId, long observedRevision, bool emptyPastePrompt, bool bracketedPaste,
        long? clearedDraft, string? retryDraft, string? screen, string? draftId)
    {
        lock (outputLock)
        {
            if (!viewers.ContainsKey(viewerId)) return;
            if (observedRevision == revision)
            {
                screens[viewerId] = (revision, screen ?? "unknown", draftId, screen == "clear" && emptyPastePrompt);
                awaitingScreenSince = null;
            }
            if (observedRevision == revision && bracketedPaste && retryDraft != null && !emptyPastePrompt)
                visibleDrafts[viewerId] = (revision, retryDraft, Stopwatch.GetTimestamp());
            else visibleDrafts.Remove(viewerId);
            if (clearedDraft is long draft && draft > clearedDraftRevision && draft < observedRevision &&
                observedRevision > clearedAtRevision && observedRevision <= revision)
            {
                clearedDraftRevision = draft;
                clearedAtRevision = observedRevision;
            }
            if (observedRevision >= pasteRevision && observedRevision <= revision)
            {
                pasteRevision = observedRevision;
                bracketedPasteEnabled = bracketedPaste;
            }
            if (emptyPastePrompt && bracketedPaste && observedRevision == revision)
            {
                draftMayContainText = false;
                // 有些按键没有可见回显。静默后再次确认同一帧为空，才解除对下一帧的等待。
                if (revision < nextPromptRevision && Stopwatch.GetElapsedTime(lastActivity) >= Quiet)
                    nextPromptRevision = revision;
                if (revision >= nextPromptRevision) ready[viewerId] = revision;
            }
            else
            {
                ready.Remove(viewerId);
                if (observedRevision == revision && (screen == "draft" || draftId != null || retryDraft != null))
                    draftMayContainText = true;
            }
            NotifyView();
        }
    }

    // 持有 outputLock。每个窗格只有一个静默到期计时器；重复的同态画面不会广播快照。
    private void Touch()
    {
        lastActivity = Stopwatch.GetTimestamp();
        if (disposed == 0) quietTimer?.Change(Quiet, Timeout.InfiniteTimeSpan);
    }

    private void NotifyView(bool recheck = false)
    {
        if (disposed != 0) return;
        var current = ViewState();
        if (!recheck && current == lastReported) return;
        lastReported = current;
        changed();
    }

    private void QuietElapsed()
    {
        lock (outputLock)
        {
            if (disposed != 0 || ended) return;
            var remaining = Quiet - Stopwatch.GetElapsedTime(lastActivity);
            if (remaining > TimeSpan.Zero)
                quietTimer?.Change(remaining + TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan);
            else NotifyView(recheck: true); // 无回显的按键也需要浏览器重新确认当前输入行。
        }
    }

    internal (bool Online, bool InputReady, string Activity) ViewState()
    {
        lock (outputLock)
        {
            if (!Online) return (false, false, "offline");
            bool inputReady = CanDeliver(true);
            // 原生 CLI 待命时也会重绘。灯读已观察的画面，自动投递仍须通过原有静默检查。
            // 浏览器解码新帧期间短暂保留上一帧的灯态，人工输入立即使旧画面失效。
            bool rendering = awaitingScreenSince is long since && Stopwatch.GetElapsedTime(since) < Quiet;
            var visible = screens.Where(p => viewers.ContainsKey(p.Key) && p.Value.Revision >= nextPromptRevision &&
                    (p.Value.Revision == revision || rendering))
                .Select(p => p.Value).OrderByDescending(s => s.Revision).ThenByDescending(s => s.Screen == "blocked")
                .ThenByDescending(s => s.Screen == "running").ThenByDescending(s => s.Idle || s.Screen == "draft").FirstOrDefault();
            string activity = visible.Screen switch
            {
                "blocked" => "waiting",
                "running" => "working",
                "draft" => "idle",
                _ => visible.Idle || inputReady ? "idle" : "working"
            };
            return (true, inputReady, activity);
        }
    }

    internal void SetMentionPicker(int viewer, bool open, int request)
    {
        lock (outputLock)
        {
            if (!open) { mentionPickers.Remove(viewer); NotifyView(); return; }
            if (!Online || !viewers.ContainsKey(viewer) || request <= 0) return;
            mentionPickers[viewer] = request;
            if (!automaticDeliveryInProgress) ConfirmMentionPicker(viewer);
            NotifyView();
        }
    }

    private void ConfirmMentionPicker(int viewer)
    {
        // 与终端帧共用该查看者的发送队列，避免同一个 WebSocket 上并发 SendAsync。
        var channel = viewers[viewer];
        if (channel.Writer.TryWrite(new TerminalFrame(0, [], MentionRequest:mentionPickers[viewer]))) mentionPickers[viewer] = 0;
        else
        {
            channel.Writer.TryComplete(new IOException("终端连接读取过慢"));
            mentionPickers.Remove(viewer);
        }
    }

    internal bool CanDeliver(bool automatic)
    {
        lock (outputLock)
            return Online && (!automatic || (mentionPickers.Count == 0 && bracketedPasteEnabled && pasteRevision == revision && !draftMayContainText &&
                ready.Values.Contains(revision) && Stopwatch.GetElapsedTime(lastActivity) >= Quiet));
    }

    private void MarkInput()
    {
        lock (outputLock)
        {
            ready.Clear();
            nextPromptRevision = revision + 1;
            Touch();
            NotifyView();
        }
    }

    internal void Resize(int cols, int rows)
    {
        lock (consoleLock)
        {
            if (!Online) return;
            if (columns == cols && lines == rows) return;
            // 调整尺寸不会编辑输入；有些 CLI 不产生新帧，允许浏览器重新确认当前帧。
            // 保留上次键盘操作已经设定的 nextPromptRevision 下限。
            lock (outputLock)
            {
                ready.Clear();
                Touch();
                NotifyView();
            }
            Marshal.ThrowExceptionForHR(Native.ResizePseudoConsole(console, new((short)cols, (short)rows)));
            columns = cols;
            lines = rows;
        }
    }

    internal async Task Input(byte[] bytes, CancellationToken ct)
    {
        await inputLock.WaitAsync(ct);
        try
        {
            if (!Online) throw new WorkbenchError("终端已退出", WorkbenchFailure.Conflict);
            if (bytes.AsSpan().Contains((byte)27))
            {
                // Latin1 保持原始字节一一对应，剔除夹在用户 UTF-8 输入中的查询回复。
                string original = Encoding.Latin1.GetString(bytes);
                string cleaned = TerminalReports.Replace(original, "");
                if (cleaned.Length == 0)
                {
                    if (Stopwatch.GetElapsedTime(launchedAt) < Quiet && !CanDeliver(true))
                    {
                        await input.WriteAsync(bytes, ct);
                        await input.FlushAsync(ct);
                    }
                    return;
                }
                if (cleaned.Length != original.Length) bytes = Encoding.Latin1.GetBytes(cleaned);
            }
            lock (outputLock)
            {
                keyboardVersion++;
                if (bytes.Length == 1 && bytes[0] is 3 or 8 or 13 or 21 or 127) draftMayContainText = false;
                else if (bytes.Length != 0 && (bytes[0] >= 32 || bytes.AsSpan().StartsWith("\u001b[200~"u8)))
                    draftMayContainText = true;
                MarkInput();
            }
            await WriteChunkedAsync(bytes, ct);
        }
        finally { inputLock.Release(); }
    }

    private async Task WriteChunkedAsync(byte[] bytes, CancellationToken ct)
    {
        const int maxChunkBytes = 32;
        if (bytes.Length <= maxChunkBytes)
        {
            await input.WriteAsync(bytes, ct);
            await input.FlushAsync(ct);
            return;
        }
        int offset = 0;
        while (offset < bytes.Length)
        {
            int end = Math.Min(offset + maxChunkBytes, bytes.Length);
            // 避免把 UTF-8 多字节字符从中间切开（continuation byte 为 10xxxxxx）。
            if (end < bytes.Length)
            {
                int safeEnd = end;
                while (safeEnd > offset && (bytes[safeEnd] & 0xC0) == 0x80) safeEnd--;
                if (safeEnd > offset) end = safeEnd;
            }
            await input.WriteAsync(bytes.AsMemory(offset, end - offset), ct);
            await input.FlushAsync(ct);
            offset = end;
            if (offset < bytes.Length) await Task.Delay(15, ct);
        }
    }

    private static readonly Regex TerminalReports = new(
        @"\x1b(?:\[(?:\??[0-9]+;[0-9]+R|[?>=][0-9;]*c|\??[0-9]+;[0-9]+\$y|[IO]|0n)|\](?:10|11|12);rgb:[a-fA-F0-9/]+(?:\x1b\\|\x07))");

    internal async Task<bool> Deliver(InboxItem item, bool automatic, Func<bool> claim, CancellationToken ct)
    {
        // 提交后允许同一 WebSocket 处理键盘或终端查询回复，继续接收浏览器的草稿清空回执。
        // 下一条外部投递须等本条确认或转为待核对，不能借用另一条消息的清空信号。
        await deliveryLock.WaitAsync(ct);
        try
        {
            var written = await PasteAndSubmit(item, automatic, claim, ct);
            if (written == null) return false;
            await WaitForSubmitConfirmation(item.Id, written.Value, ct);
            return true;
        }
        finally
        {
            lock (outputLock)
            {
                if (automaticDeliveryInProgress)
                {
                    automaticDeliveryInProgress = false;
                    foreach (int viewer in mentionPickers.Where(pair => pair.Value > 0).Select(pair => pair.Key).ToArray())
                        ConfirmMentionPicker(viewer);
                }
            }
            deliveryLock.Release();
        }
    }

    private async Task<(long BeforePaste, long BeforeSubmit, long KeyboardVersion)?> PasteAndSubmit(InboxItem item, bool automatic,
        Func<bool> claim, CancellationToken ct)
    {
        await inputLock.WaitAsync(ct);
        try
        {
            bool useBracketedPaste;
            long beforePaste;
            lock (outputLock)
            {
                // 最后一次空闲检查与认领共用预留区，避免已被新指令取代的旧消息回退到 pending。
                if (!CanDeliver(automatic) || !claim()) return null;
                useBracketedPaste = bracketedPasteEnabled;
                beforePaste = revision;
                automaticDeliveryInProgress = automatic;
                MarkInput();
                draftMayContainText = false;
            }
            // 标记单独写入，正文仍按完整 UTF-8 字符分片，以保护 Windows 10 ConPTY。
            string text = item.AsText();
            if (useBracketedPaste) await WriteChunkedAsync("\u001b[200~"u8.ToArray(), ct);
            await WriteChunkedAsync(Encoding.UTF8.GetBytes(text), ct);
            if (useBracketedPaste) await WriteChunkedAsync("\u001b[201~"u8.ToArray(), ct);
            // 只有末尾仍是 @ 补全词才结束补全，且与粘贴结束标记分开发送。
            if (Regex.IsMatch(text, @"(?:^|[\s；：])@[\p{L}\p{M}\p{N}_./\\-]*[\p{L}\p{M}\p{N}_/\\-]\z"))
            {
                await Task.Delay(80, ct);
                await WriteChunkedAsync(" "u8.ToArray(), ct);
            }
            long afterPaste;
            lock (outputLock) afterPaste = revision;
            await WaitForPasteRedraw(afterPaste, ct);
            long beforeSubmit;
            lock (outputLock)
            {
                if (!Online) throw new IOException("终端在提交前退出");
                beforeSubmit = revision;
            }
            await input.WriteAsync(new byte[] { 13 }, ct);
            await input.FlushAsync(ct);
            return (beforePaste, beforeSubmit, keyboardVersion);
        }
        finally { inputLock.Release(); }
    }

    private async Task WaitForPasteRedraw(long afterPaste, CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            lock (outputLock)
            {
                if (!Online) throw new IOException("终端在提交前退出");
                // Codex 在 Windows 上会把粘贴后的短间隔回车当作正文中的换行。
                // 没有新帧时留足 1.8 秒；有积压重绘时，刷管道并不等于 CLI 已消费完输入。
                if (elapsed.TotalMilliseconds >= 800 && Stopwatch.GetElapsedTime(lastActivity).TotalMilliseconds >= 350 &&
                    (revision > afterPaste || elapsed.TotalMilliseconds >= 1800))
                    return;
                if (elapsed.TotalSeconds >= 20)
                    throw new IOException("终端仍在重绘粘贴内容，尚未发送回车，请核对终端");
            }
            await Task.Delay(30, ct);
        }
    }

    private async Task WaitForSubmitConfirmation(string messageId, (long BeforePaste, long BeforeSubmit, long KeyboardVersion) sent, CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();
        bool retried = false;
        while (true)
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            bool retry;
            lock (outputLock)
            {
                if (!Online) throw new IOException("终端在提交后退出");
                // [T09c] 回车后用户可以继续输入。认定送达依赖清空、原生执行提示或已被其他消息替代的草稿。
                // 单凭输出序号增加、观察端掉线或一次无法识别的画面，无法证明当前正文已离开编辑器。
                if (elapsed.TotalMilliseconds >= 250 && revision > sent.BeforeSubmit)
                {
                    if (clearedDraftRevision > sent.BeforePaste && clearedAtRevision > sent.BeforeSubmit) return;
                    var current = screens.Where(p => viewers.ContainsKey(p.Key) && p.Value.Revision == revision).Select(p => p.Value);
                    if (!current.Any(s => s.Draft == messageId[..8]) && current.Any(s =>
                        s.Screen is "clear" or "running" ||
                        (s.Screen == "draft" && (s.Draft != null || keyboardVersion != sent.KeyboardVersion)))) return;
                }
                // Copilot 提交长文后会暂时把下分隔线移出视口；实测完整空输入框在 4 秒窗口之后才重现。
                // 仅给在线观察者看到过提交后重绘的情况延长回执时间，期间仍须取得清空证据。
                int confirmationSeconds = viewers.Count > 0 && revision > sent.BeforeSubmit &&
                    keyboardVersion == sent.KeyboardVersion ? 8 : 4;
                if (elapsed.TotalSeconds >= confirmationSeconds)
                {
                    draftMayContainText = true;
                    throw new IOException("已发送回车，未观察到原生输入行清空，请核对终端");
                }
                retry = !retried && elapsed.TotalMilliseconds >= 1100 && StillEditing();
            }
            if (retry)
            {
                await inputLock.WaitAsync(ct);
                try
                {
                    lock (outputLock)
                    {
                        if (!StillEditing()) continue;
                        // 同一写锁内复核最新草稿与键盘版本，然后只补回车，不重写正文或再次认领。
                        retried = true;
                        sent.BeforeSubmit = revision;
                    }
                    await input.WriteAsync(new byte[] { 13 }, ct);
                    await input.FlushAsync(ct);
                    started = Stopwatch.GetTimestamp();
                }
                finally { inputLock.Release(); }
            }
            await Task.Delay(30, ct);
        }

        // 首次回车也可能被直接吞掉或变成换行；无新帧时复用粘贴重绘已确认的草稿，有新帧时允许按完整 ID 或 8 位前缀匹配当前草稿。
        bool StillEditing() => Online && keyboardVersion == sent.KeyboardVersion && revision > sent.BeforePaste &&
            (clearedDraftRevision <= sent.BeforePaste || clearedAtRevision <= sent.BeforeSubmit) &&
            Stopwatch.GetElapsedTime(lastActivity).TotalMilliseconds >= 250 &&
            (revision == sent.BeforeSubmit || visibleDrafts.Any(pair => viewers.ContainsKey(pair.Key) &&
                pair.Value.Revision == revision &&
                (pair.Value.Message == messageId || pair.Value.Message == messageId[..8]) &&
                pair.Value.ObservedAt >= started));
    }

    public void Dispose()
    {
        lock (consoleLock)
        {
            if (job.Handle.IsClosed) return;
            if (disposed == 0)
            {
                lock (outputLock)
                {
                    exitCode = ReadExitCode();
                    Volatile.Write(ref disposed, 1);
                    quietTimer?.Dispose();
                }
                Native.ClosePseudoConsole(console);
                // 保持输出读取线程运行，让 CLI 有机会完成原生关闭和历史落盘。
                Native.WaitForSingleObject(process, 2000);
            }
            job.TerminateRemaining();
            input.Dispose();
            drain.GetAwaiter().GetResult();
            output.Dispose();
            process.Dispose();
            job.Dispose();
        }
    }

    private SafeProcessHandle Launch(string executable, IReadOnlyList<string> arguments, string cwd, string grant,
        IReadOnlyDictionary<string, string>? overrides)
    {
        nint size = 0;
        Native.InitializeProcThreadAttributeList(0, 2, 0, ref size);
        nint attributes = Marshal.AllocHGlobal(size + IntPtr.Size);
        nint jobList = attributes + size;
        bool initialized = false;
        nint environment = 0;
        try
        {
            if (!Native.InitializeProcThreadAttributeList(attributes, 2, 0, ref size))
                throw Native.Error("无法初始化终端进程属性");
            initialized = true;
            if (!Native.UpdateProcThreadAttribute(attributes, 0, 0x00020016, console, IntPtr.Size, 0, 0))
                throw Native.Error("无法将进程连接到 ConPTY");
            // PROC_THREAD_ATTRIBUTE_JOB_LIST 在创建进程时生效，消除创建与加入 Job 之间宿主退出的窗口。
            Marshal.WriteIntPtr(jobList, job.Handle.DangerousGetHandle());
            if (!Native.UpdateProcThreadAttribute(attributes, 0, 0x0002000D, jobList, IntPtr.Size, 0, 0))
                throw Native.Error("无法将进程绑定到窗格");

            var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
                variables[(string)variable.Key] = (string)variable.Value!;
            if (overrides != null)
                foreach (var pair in overrides) variables[pair.Key] = pair.Value;
            variables["FF_P0_WINDOW_GRANT"] = variables["LX_P0_WINDOW_GRANT"] = grant;
            variables["TERM"] = "xterm-256color";
            environment = Marshal.StringToHGlobalUni(string.Join('\0', variables.Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0");
            var startup = new Native.StartupInfoEx
            {
                // Windows 10 宿主重定向标准流时，显式传空句柄让 ConPTY 分配控制台标准流。
                Info = new Native.StartupInfo { Size = Marshal.SizeOf<Native.StartupInfoEx>(), Flags = 0x00000100 },
                AttributeList = attributes
            };
            var start = WindowsCommand.Resolve(executable, arguments);
            executable = start.FileName;
            var command = new StringBuilder(QuoteArgument(executable) + " " + start.Arguments);
            if (!Native.CreateProcess(executable, command, 0, 0, false, 0x00080000 | 0x00000400,
                environment, cwd, ref startup, out var handles))
                throw Native.Error("无法启动原生 CLI");
            using var initialThread = new SafeFileHandle(handles.Thread, ownsHandle: true);
            return new SafeProcessHandle(handles.Process, ownsHandle: true);
        }
        finally
        {
            if (initialized) Native.DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
            if (environment != 0) Marshal.FreeHGlobal(environment);
        }
    }

    internal static string QuoteArgument(string argument)
    {
        var quoted = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            quoted.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character);
            slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal readonly record struct Coord(short X, short Y);

        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfo
        {
            internal int Size;
            internal nint Reserved, Desktop, Title;
            internal int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            internal ushort ShowWindow, ReservedLength;
            internal nint ReservedBytes, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfoEx
        {
            internal StartupInfo Info;
            internal nint AttributeList;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInfo
        {
            internal nint Process, Thread;
            internal int ProcessId, ThreadId;
        }

        internal static Exception Error(string action) =>
            new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), action);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint attributes, int size);
        // 优先使用随程序分发的 Microsoft.Windows.Console.ConPTY（conpty.dll + OpenConsole.exe），
        // 绕开 Windows 10 自带 conhost.exe 在长中文粘贴时的 0xc0000409 崩溃。
        [DllImport("conpty.dll", EntryPoint = "ConptyCreatePseudoConsole")]
        internal static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint console);
        [DllImport("conpty.dll", EntryPoint = "ConptyResizePseudoConsole")]
        internal static extern int ResizePseudoConsole(nint console, Coord size);
        [DllImport("conpty.dll", EntryPoint = "ConptyClosePseudoConsole")]
        internal static extern void ClosePseudoConsole(nint console);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nint size, nint previous, nint returned);
        [DllImport("kernel32.dll")]
        internal static extern void DeleteProcThreadAttributeList(nint list);
        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool CreateProcess(string application, StringBuilder command, nint processAttributes, nint threadAttributes,
            bool inheritHandles, uint flags, nint environment, string directory, ref StartupInfoEx startup, out ProcessInfo process);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    }
}
