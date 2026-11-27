using System.Diagnostics;
using System.Collections.Concurrent;
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
internal sealed partial class TerminalSession : IDisposable
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
    private readonly Dictionary<int, (long Revision, string Message, long ObservedAt, string? Tail, int? FoldedChars, long StableSince)> visibleDrafts = new();
    private readonly FileStream input;
    private readonly FileStream output;
    private readonly SafeProcessHandle process;
    private readonly TerminalJob job;
    private readonly Task drain;
    private nint console;
    private long revision;
    private long nextPromptRevision;
    private long pasteRevision = -1;
    private long lastFoldedRevision = -1;
    private long clearedDraftRevision = -1;
    private long clearedAtRevision = -1;
    private long keyboardVersion;
    private long cleanEditorIntent;
    private long submittedEditorIntent;
    private long submittedEditorRevision;
    private long keyboardIntent;
    private long deliveryEpoch;
    private readonly long launchedAt = Stopwatch.GetTimestamp();
    private readonly Dictionary<int, (long Revision, string Screen, string? Draft, bool Idle, string? ApprovalDraft, bool BusyInput, string? QueuedId)> screens = new();
    private long lastKeyboardInput;
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
    private readonly bool workingWithoutPrompt;
    private readonly bool codexEditor;
    private readonly ISubmissionReceiptSource? submissionReceipts;
    private sealed class DeliveryReview(string tail, int expectedChars, ISubmissionReceipt? receipt)
    {
        internal readonly string Tail = tail;
        internal readonly int ExpectedChars = expectedChars;
        internal readonly ISubmissionReceipt? Receipt = receipt;
        internal long BeforePaste, BeforeSubmit = -1, KeyboardVersion, KeyboardIntent, Epoch, Since, LastCheck;
        internal bool BusyAtStart;
        internal int Checks;
    }

    private readonly ConcurrentDictionary<string, DeliveryReview> recentReceipts = new();
    private readonly Timer? quietTimer;
    private (bool Online, bool InputReady, string Activity)? lastReported;
    private bool lastBusyReady;

    internal TerminalSession(string engine, string executable, IReadOnlyList<string> arguments, string cwd, string grant, int cols, int rows, Action changed,
        IReadOnlyDictionary<string, string>? environment = null, ISubmissionReceiptSource? submissionReceipts = null)
    {
        this.changed = changed;
        this.submissionReceipts = submissionReceipts;
        codexEditor = engine is "codex" or "traecli";
        unsubmittedNewSession = engine is "claude" or "qoder" && arguments.Contains("--session-id") && !arguments.Contains("--fork-session");
        workingWithoutPrompt = engine is "codex" or "copilot";
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
    internal bool NativeInbox { get; init; }
    internal Func<string, CancellationToken, Task>? Enqueue { get; init; }
    private bool unsubmittedNewSession;
    internal bool CanReopenEmpty { get { lock (outputLock) return unsubmittedNewSession; } }
    // 调用方在终端释放前注销等待。登录 CLI 可能先退出，ConPTY 的输出管道仍然打开。
    internal WaitHandle ExitSignal() => new EventWaitHandle(false, EventResetMode.ManualReset) {
        SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle:false)
    };
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
            composingViewers.Remove(id);
            mentionPickers.Remove(id);
            if (viewers.Remove(id, out var channel)) channel.Writer.TryComplete();
            NotifyView();
        }
    }

    internal void ObservePrompt(int viewerId, long observedRevision, bool emptyPastePrompt, bool bracketedPaste,
        long? clearedDraft, string? retryDraft, string? screen, string? draftId, string? approvalDraft, string? draftTail,
        int? foldedChars, bool busyInput, string? queuedId, bool placeholderCandidate)
    {
        lock (outputLock)
        {
            if (!viewers.ContainsKey(viewerId)) return;
            long intent = Volatile.Read(ref keyboardIntent);
            if (observedRevision == revision && revision > submittedEditorRevision && submittedEditorIntent == intent &&
                !placeholderCandidate && bracketedPaste && (emptyPastePrompt && screen == "clear" || busyInput && screen is ("active" or "running")))
                cleanEditorIntent = intent;
            if (placeholderCandidate && observedRevision == revision && bracketedPaste && screen == "draft" &&
                draftId == null && retryDraft == null && intent == cleanEditorIntent)
            {
                screen = "clear";
                emptyPastePrompt = true;
            }
            else if (placeholderCandidate) clearedDraft = null;
            if (observedRevision == revision)
            {
                bool busyEditor = busyInput && bracketedPaste && screen is ("active" or "running") && draftId == null;
                screens[viewerId] = (revision, screen ?? "unknown", draftId, screen == "clear" && emptyPastePrompt, approvalDraft, busyEditor, queuedId);
                if (busyEditor && revision >= nextPromptRevision) draftMayContainText = false;
                awaitingScreenSince = null;
                if (bracketedPaste && retryDraft != null && !emptyPastePrompt)
                {
                    long now = Stopwatch.GetTimestamp();
                    string? tail = draftTail is { Length: > 0 and <= 48 } ? draftTail : null;
                    foldedChars = codexEditor ? foldedChars : null;
                    if (foldedChars.HasValue) lastFoldedRevision = revision;
                    long stable = visibleDrafts.TryGetValue(viewerId, out var prior) && prior.Message == retryDraft && prior.Tail == tail && prior.FoldedChars == foldedChars
                        ? prior.StableSince : now;
                    visibleDrafts[viewerId] = (revision, retryDraft, now, tail, foldedChars, stable);
                }
                else visibleDrafts.Remove(viewerId);
            }
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
        bool busyReady = current.Activity == "working" && CanDeliver(true, immediate: true);
        if (!recheck && current == lastReported && busyReady == lastBusyReady) return;
        lastBusyReady = busyReady;
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

    internal bool MayEnqueue(bool immediate)
    {
        lock (outputLock)
        {
            // With no screen observer we have no idle signal. Leave scheduling to the CLI's thread queue.
            return Online && (immediate || viewers.Count == 0 || ViewState().Activity == "idle");
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
                .ThenByDescending(s => s.Screen is "running" or "active").ThenByDescending(s => s.Idle || s.Screen is "draft" or "idle").FirstOrDefault();
            string activity = visible.Screen switch
            {
                "blocked" => "waiting",
                "running" or "active" => "working",
                "draft" or "idle" => "idle",
                _ => visible.Idle || inputReady || !workingWithoutPrompt ? "idle" : "working"
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

    internal bool CanDeliver(bool automatic, bool immediate = false)
    {
        lock (outputLock)
        {
            if (!Online || mentionPickers.Count != 0 || composingViewers.Count != 0 || draftMayContainText) return false;
            if (!automatic) return !screens.Values.Any(s => s.Revision == revision && s.Screen is "blocked" or "draft");
            return bracketedPasteEnabled && pasteRevision == revision &&
                ((ready.Values.Contains(revision) && Stopwatch.GetElapsedTime(lastActivity) >= Quiet) ||
                 (immediate && revision >= nextPromptRevision && Stopwatch.GetElapsedTime(lastKeyboardInput) >= Quiet &&
                  screens.Values.Any(s => s.Revision == revision && s.BusyInput) &&
                  !screens.Values.Any(s => s.Revision == revision && (s.Screen is "blocked" or "draft" || s.Draft != null))));
        }
    }

    // 只有本次回车之后、原生队列明确显示当前编号，才算 CLI 已接手。
    private bool NativeQueuedSince(string messageId, long beforeSubmit) => revision > beforeSubmit &&
        screens.Values.Any(s => s.Revision == revision && s.QueuedId == messageId[..8]);

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
        // 先识别人类输入。即使用户按键正在等待写锁，自动修复也必须让出编辑器。
        byte[]? terminalReply = null;
        if (bytes.AsSpan().Contains((byte)27))
        {
            string original = Encoding.Latin1.GetString(bytes);
            string cleaned = TerminalReports.Replace(original, "");
            if (cleaned.Length == 0) terminalReply = bytes;
            else if (cleaned.Length != original.Length) bytes = Encoding.Latin1.GetBytes(cleaned);
        }
        long intent = terminalReply == null ? Interlocked.Increment(ref keyboardIntent) : 0;
        await inputLock.WaitAsync(ct);
        try
        {
            if (!Online) throw new WorkbenchError("终端已退出", WorkbenchFailure.Conflict);
            if (terminalReply != null)
            {
                if (Stopwatch.GetElapsedTime(launchedAt) < Quiet && !CanDeliver(true))
                {
                    await input.WriteAsync(terminalReply, ct);
                    await input.FlushAsync(ct);
                }
                return;
            }
            lock (outputLock)
            {
                TrackTypedLine(bytes, intent);
                keyboardVersion++;
                // A later unambiguous empty editor can release ownership after Enter or Ctrl+U.
                if (bytes.Length == 1 && bytes[0] is 13 or 21)
                {
                    submittedEditorIntent = intent;
                    submittedEditorRevision = revision;
                }
                lastKeyboardInput = Stopwatch.GetTimestamp();
                if (bytes.AsSpan().IndexOfAny((byte)10, (byte)13) >= 0) unsubmittedNewSession = false;
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

    internal async Task OpenApprovalMenu(string keys, CancellationToken ct)
    {
        await inputLock.WaitAsync(ct);
        try
        {
            lock (outputLock)
            {
                if (!CanDeliver(true) || automaticDeliveryInProgress)
                    throw new WorkbenchError("请在终端待命且输入框为空时切换审批模式", WorkbenchFailure.Conflict);
                keyboardVersion++;
                MarkInput();
            }
            bool submit = keys.EndsWith('\r');
            string command = submit ? keys[..^1] : keys;
            await WriteChunkedAsync(Encoding.UTF8.GetBytes(command), ct);
            if (submit)
            {
                // 回车只发给仍显示这条命令的编辑器。切到原生弹窗后不代替用户确认选项。
                long started = Stopwatch.GetTimestamp();
                do
                {
                    await Task.Delay(80, ct);
                    lock (outputLock)
                    {
                        if (!Online) throw new WorkbenchError("终端已退出", WorkbenchFailure.Conflict);
                        if (Stopwatch.GetElapsedTime(started).TotalMilliseconds < 600 ||
                            !screens.Values.Any(s => s.Revision == revision && s.Screen == "draft" && s.ApprovalDraft == command)) continue;
                        input.WriteByte(13);
                        input.Flush();
                        return;
                    }
                } while (Stopwatch.GetElapsedTime(started).TotalSeconds < 2);
                throw new WorkbenchError("已输入审批命令，请在原生终端确认当前选项", WorkbenchFailure.Conflict);
            }
        }
        finally { inputLock.Release(); }
    }

    internal async Task CloseForSettingsChange(Action revokeGrant, bool reopeningEmpty, CancellationToken ct)
    {
        // 让已提交的收件先完成投递确认，避免改变终端后丢失回执。
        if (!await deliveryLock.WaitAsync(0, ct))
            throw new WorkbenchError("正在投递消息，请在送达后再切换设置", WorkbenchFailure.Conflict);
        try
        {
            await inputLock.WaitAsync(ct);
            try
            {
                if (reopeningEmpty && !CanReopenEmpty)
                    throw new WorkbenchError("会话刚收到新输入，请稍后再切换", WorkbenchFailure.Conflict);
                revokeGrant();
                await Task.Run(Dispose);
            }
            finally { inputLock.Release(); }
        }
        finally { deliveryLock.Release(); }
    }

    private static readonly Regex TerminalReports = new(
        @"\x1b(?:\[(?:\??[0-9]+;[0-9]+R|[?>=][0-9;]*c|\??[0-9]+;[0-9]+\$y|[IO]|0n)|\](?:10|11|12);rgb:[a-fA-F0-9/]+(?:\x1b\\|\x07))");

    internal async Task<bool> Deliver(InboxItem item, bool automatic, Func<bool> claim, CancellationToken ct, bool immediate = false, ManualEditorDraft? draft = null)
    {
        // 提交后允许同一 WebSocket 处理键盘或终端查询回复，继续接收浏览器的草稿清空回执。
        // 下一条外部投递须等本条确认或转为待核对，不能借用另一条消息的清空信号。
        await deliveryLock.WaitAsync(ct);
        ISubmissionReceipt? receipt = null;
        DeliveryReview? review = null;
        bool claimed = false;
        try
        {
            string text = item.AsText();
            var compact = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
            string fullTail = compact.Length <= 48 ? compact : compact[^48..];
            string? tail = codexEditor || immediate ? fullTail : null;
            long? draftIntent = null;
            if (draft != null)
            {
                if (automatic) return false;
                draftIntent = await ClearManualDraft(draft, ct);
                if (draftIntent == null) return false;
                // After temporarily clearing an editor, require fresh empty and keyboard evidence at claim and submit.
                automatic = true;
                immediate = false;
            }
            if (!CanDeliver(automatic, immediate)) return false;
            receipt = submissionReceipts?.Begin(item.Id, text);
            int expectedChars = text.EnumerateRunes().Count();
            review = new(fullTail, expectedChars, receipt);
            bool Claim()
            {
                if (draftIntent != null && draftIntent != Volatile.Read(ref keyboardIntent)) return false;
                return claimed = claim();
            }
            var written = await PasteAndSubmit(item.Id, text, tail, expectedChars, automatic, Claim, ct, immediate, review);
            if (written == null) return false;
            await WaitForSubmitConfirmation(item.Id, tail, expectedChars, written.Value, receipt, review, ct);
            if (draft != null && draftIntent.HasValue)
            {
                draft.Restored = await RestoreManualDraft(draft.Text, draftIntent.Value, ct);
                if (!draft.Restored)
                    lock (outputLock) deferredDraft = (item.Id, draftIntent.Value, deliveryEpoch,
                        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(draft.Text)));
            }
            return true;
        }
        catch
        {
            if (claimed && review != null)
            {
                review.LastCheck = review.Since = Stopwatch.GetTimestamp();
                recentReceipts[item.Id] = review;
            }
            throw;
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

    private async Task<(long BeforePaste, long BeforeSubmit, long KeyboardVersion, long KeyboardIntent, bool BusyAtStart, bool Folded)?> PasteAndSubmit(string messageId, string text, string? tail, int expectedChars, bool automatic,
        Func<bool> claim, CancellationToken ct, bool immediate, DeliveryReview review)
    {
        await inputLock.WaitAsync(ct);
        try
        {
            bool useBracketedPaste;
            bool busyAtStart;
            long beforePaste;
            long keyboardAtPaste;
            lock (outputLock)
            {
                keyboardAtPaste = Volatile.Read(ref keyboardIntent);
                // 最后一次空闲检查与认领共用预留区，避免已被新指令取代的旧消息回退到 pending。
                if (!CanDeliver(automatic, immediate) || !claim()) return null;
                busyAtStart = automatic && immediate && !CanDeliver(true);
                useBracketedPaste = bracketedPasteEnabled;
                beforePaste = revision;
                review.BeforePaste = beforePaste;
                review.Epoch = ++deliveryEpoch;
                review.KeyboardIntent = keyboardAtPaste;
                review.KeyboardVersion = keyboardVersion;
                review.BusyAtStart = busyAtStart;
                automaticDeliveryInProgress = automatic;
                MarkInput();
                ResetTypedLine();
                draftMayContainText = false;
            }
            async Task Paste()
            {
                // 标记单独写入，正文仍按完整 UTF-8 字符分片，以保护 Windows 10 ConPTY。
                if (useBracketedPaste) await WriteChunkedAsync("\u001b[200~"u8.ToArray(), ct);
                await WriteChunkedAsync(Encoding.UTF8.GetBytes(text), ct);
                if (useBracketedPaste) await WriteChunkedAsync("\u001b[201~"u8.ToArray(), ct);
            }
            await Paste();
            // 只有末尾仍是 @ 补全词才结束补全，且与粘贴结束标记分开发送。
            if (Regex.IsMatch(text, @"(?:^|[\s；：])@[\p{L}\p{M}\p{N}_./\\-]*[\p{L}\p{M}\p{N}_/\\-]\z"))
            {
                await Task.Delay(80, ct);
                await WriteChunkedAsync(" "u8.ToArray(), ct);
            }
            long afterPaste;
            lock (outputLock) afterPaste = revision;
            if (!await WaitForPasteRedraw(beforePaste, afterPaste, messageId, tail, expectedChars, ct, busyAtStart))
            {
                if (!automatic || !useBracketedPaste || !await ClearOwnedDraft(messageId, tail!, beforePaste, keyboardAtPaste, ct))
                    throw new IOException("终端草稿与本次投递不一致，请核对终端");
                lock (outputLock) review.BeforePaste = beforePaste = revision;
                await Paste();
                lock (outputLock) afterPaste = revision;
                if (!await WaitForPasteRedraw(beforePaste, afterPaste, messageId, tail, expectedChars, ct, busyAtStart, requireComplete:true))
                    throw new IOException("重写后仍未识别完整草稿，请核对终端");
            }
            long beforeSubmit;
            bool folded;
            lock (outputLock)
            {
                if (!Online) throw new IOException("终端在提交前退出");
                if (automatic && (Volatile.Read(ref keyboardIntent) != keyboardAtPaste || composingViewers.Count != 0 ||
                    screens.Values.Any(s => s.Revision == revision && s.Screen == "blocked")))
                    throw new IOException("已观察到人工输入或审批，暂停自动提交");
                folded = lastFoldedRevision > beforePaste;
                if ((busyAtStart || folded) && !CompleteDraftStable(messageId, tail, expectedChars, beforePaste))
                    throw new IOException("原生编辑器已切换，未向新界面发送回车");
                beforeSubmit = revision;
                review.BeforeSubmit = beforeSubmit;
                unsubmittedNewSession = false;
            }
            await input.WriteAsync(new byte[] { 13 }, ct);
            await input.FlushAsync(ct);
            return (beforePaste, beforeSubmit, keyboardVersion, keyboardAtPaste, busyAtStart, folded);
        }
        finally { inputLock.Release(); }
    }

    // 每一条证据都来自当前查看者对最新画面的观察。按消息头与尾部核对，排除部分重绘或其他消息。
    // Codex 长粘贴会折叠为 [Pasted Content N chars]，此时按本次粘贴后的新帧与字符量级核对。
    private bool CompleteDraftStable(string id, string? tail, int expectedChars, long beforePaste)
        => tail != null && visibleDrafts.Values.Any(d => d.Revision == revision && d.Revision > beforePaste &&
            (d.FoldedChars is int chars ? chars == expectedChars : d.Tail == tail) &&
            (d.Message == id || d.Message == id[..8]) && Stopwatch.GetElapsedTime(d.StableSince).TotalMilliseconds >= 350);

    private async Task<bool> WaitForPasteRedraw(long beforePaste, long afterPaste, string messageId, string? tail, int expectedChars,
        CancellationToken ct, bool busyAtStart, bool requireComplete = false)
    {
        long started = Stopwatch.GetTimestamp();
        long progress = started;
        string? lastTail = null;
        while (true)
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            lock (outputLock)
            {
                if (!Online) throw new IOException("终端在提交前退出");
                // Codex 在 Windows 上会把粘贴后的短间隔回车当作正文中的换行。
                if (lastFoldedRevision > beforePaste && screens.Values.Any(s => s.Revision == revision && s.Screen == "blocked"))
                    throw new IOException("终端已进入审批界面，尚未发送回车，请核对终端");
                // 没有新帧时留足 1.8 秒；有积压重绘时，刷管道并不等于 CLI 已消费完输入。
                var observed = tail == null ? null : visibleDrafts.Values.FirstOrDefault(d => d.Revision == revision &&
                    (d.Message == messageId || d.Message == messageId[..8])).Tail;
                if (observed != null && observed != lastTail)
                {
                    progress = Stopwatch.GetTimestamp();
                    lastTail = observed;
                }
                bool quiet = Stopwatch.GetElapsedTime(lastActivity).TotalMilliseconds >= 350 &&
                    (revision > afterPaste || elapsed.TotalMilliseconds >= 1800);
                if (elapsed.TotalMilliseconds >= 800 && (CompleteDraftStable(messageId, tail, expectedChars, beforePaste) ||
                    (!requireComplete && !busyAtStart && lastFoldedRevision <= beforePaste && quiet && (observed == null || observed == tail))))
                    return true;
                if (requireComplete && elapsed.TotalSeconds >= 4) return false;
                if (codexEditor && observed != null && observed != tail && !busyAtStart &&
                    elapsed.TotalMilliseconds >= 1800 && Stopwatch.GetElapsedTime(progress).TotalMilliseconds >= 1200 &&
                    OwnedMalformedDraft(messageId, tail!, beforePaste)) return false;
                if (Stopwatch.GetElapsedTime(progress).TotalSeconds >= 20 || elapsed.TotalSeconds >= 60)
                    throw new IOException("终端仍在重绘粘贴内容，尚未发送回车，请核对终端");
            }
            await Task.Delay(30, ct);
        }
    }

    // 粘贴尚未发过回车时，只清理当前空闲编辑器内、含本条编号且尾部不匹配的未折叠草稿。
    private bool OwnedMalformedDraft(string id, string tail, long beforePaste) =>
        revision > beforePaste && Stopwatch.GetElapsedTime(lastActivity).TotalMilliseconds >= 350 &&
        screens.Values.Any(s => s.Revision == revision && s.Screen == "draft" && s.Draft == id[..8]) &&
        !screens.Values.Any(s => s.Revision == revision && s.Screen is "blocked" or "running" or "active") &&
        visibleDrafts.Values.Any(d => d.Revision == revision && d.Message == id && d.FoldedChars == null &&
            d.Tail != null && d.Tail != tail && Stopwatch.GetElapsedTime(d.StableSince).TotalMilliseconds >= 1200);

    // 调用方持有 inputLock，直到原生空编辑器出现后才可重写。退出或失去画面证据时保留待核对。
    private async Task<bool> ClearOwnedDraft(string id, string tail, long beforePaste, long keyboardAtPaste, CancellationToken ct)
    {
        long beforeClear;
        lock (outputLock)
        {
            if (!Online || Volatile.Read(ref keyboardIntent) != keyboardAtPaste || !OwnedMalformedDraft(id, tail, beforePaste)) return false;
            beforeClear = revision;
        }
        await input.WriteAsync(new byte[] { 3 }, ct);
        await input.FlushAsync(ct);
        long started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started).TotalSeconds < 2)
        {
            lock (outputLock)
            {
                if (!Online || Volatile.Read(ref keyboardIntent) != keyboardAtPaste) return false;
                if (revision > beforeClear && screens.Values.Any(s => s.Revision == revision && s.Idle) &&
                    !screens.Values.Any(s => s.Revision == revision && s.Screen != "clear")) return true;
            }
            await Task.Delay(30, ct);
        }
        return false;
    }

    internal async Task<IReadOnlyList<string>> ReconcileReceipts(Func<string, bool> stillUncertain, CancellationToken ct)
    {
        var confirmed = new List<string>();
        if (recentReceipts.IsEmpty || !await deliveryLock.WaitAsync(0, ct)) return confirmed;
        try
        {
            foreach (var (id, saved) in recentReceipts)
            {
                if (Stopwatch.GetElapsedTime(saved.LastCheck).TotalSeconds < 5) continue;
                if (!Online || !stillUncertain(id) || Stopwatch.GetElapsedTime(saved.Since).TotalMinutes > 2)
                {
                    recentReceipts.TryRemove(id, out _);
                    continue;
                }
                saved.LastCheck = Stopwatch.GetTimestamp();
                if (saved.Checks <= 5)
                {
                    saved.Checks++;
                    if (await ReviewSubmission(id, saved, stillUncertain, ct)) confirmed.Add(id);
                    else if (saved.Checks > 5 && saved.Receipt == null) recentReceipts.TryRemove(id, out _);
                }
                else if (saved.Receipt?.Check() == SubmissionStatus.Confirmed) confirmed.Add(id);
            }
        }
        finally { deliveryLock.Release(); }
        return confirmed;
    }

    private async Task<bool> ReviewSubmission(string id, DeliveryReview review, Func<string, bool> stillUncertain, CancellationToken ct)
    {
        // 后续核对先查原生回执。只有当前屏幕仍有本条完整草稿时才能补交，绝不重贴正文。
        var receipt = review.Receipt?.Check(force: true) ?? SubmissionStatus.Unavailable;
        if (receipt == SubmissionStatus.Confirmed) return true;
        if (!await inputLock.WaitAsync(0, ct)) return false;
        try
        {
            if (!stillUncertain(id)) return false;
            lock (outputLock)
            {
                if (review.BeforeSubmit >= 0 && NativeQueuedSince(id, review.BeforeSubmit)) return true;
                // 后续自动投递不会改变键盘计数，仍须禁止用后一条消息的清空帧确认本条。
                if (!Online || review.Epoch != deliveryEpoch || Volatile.Read(ref keyboardIntent) != review.KeyboardIntent || keyboardVersion != review.KeyboardVersion)
                    return false;
                if (receipt == SubmissionStatus.Unavailable && review.BeforeSubmit >= 0 && !review.BusyAtStart &&
                    clearedDraftRevision > review.BeforePaste && clearedAtRevision > review.BeforeSubmit) return true;
                // 第五次补交之后，再保留一次只读机会确认最后回车的结果。
                if (review.Checks > 5 || screens.Values.Any(s => s.Revision == revision && s.Screen is "blocked" or "running" or "active") ||
                    !CompleteDraftStable(id, review.Tail, review.ExpectedChars, review.BeforePaste))
                    return false;
                review.BeforeSubmit = revision;
            }
            await input.WriteAsync(new byte[] { 13 }, ct);
            await input.FlushAsync(ct);
            return false;
        }
        finally { inputLock.Release(); }
    }

    internal void RemoveReceipt(string id) => recentReceipts.TryRemove(id, out _);

    private async Task WaitForSubmitConfirmation(string messageId, string? tail, int expectedChars,
        (long BeforePaste, long BeforeSubmit, long KeyboardVersion, long KeyboardIntent, bool BusyAtStart, bool Folded) sent, ISubmissionReceipt? receipt, DeliveryReview review, CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();
        bool retried = false;
        while (true)
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            bool retry;
            var native = receipt?.Check() ?? SubmissionStatus.Unavailable;
            if (native == SubmissionStatus.Confirmed) return;
            lock (outputLock)
            {
                if (NativeQueuedSince(messageId, sent.BeforeSubmit)) return;
                if (!Online) throw new IOException("终端在提交后退出");
                // [T09c] 回车后用户可以继续输入。认定送达依赖清空、原生执行提示或已被其他消息替代的草稿。
                // 单凭输出序号增加、观察端掉线或一次无法识别的画面，无法证明当前正文已离开编辑器。
                if (native == SubmissionStatus.Unavailable && elapsed.TotalMilliseconds >= 250 && revision > sent.BeforeSubmit)
                {
                    if (clearedDraftRevision > sent.BeforePaste && clearedAtRevision > sent.BeforeSubmit) return;
                    var current = screens.Where(p => viewers.ContainsKey(p.Key) && p.Value.Revision == revision).Select(p => p.Value);
                    if (!sent.BusyAtStart && !current.Any(s => s.Draft == messageId[..8]) && current.Any(s =>
                        s.Screen is "clear" or "running" ||
                        (s.Screen == "draft" && (s.Draft != null || keyboardVersion != sent.KeyboardVersion)))) return;
                }
                // Copilot 提交长文后会暂时把下分隔线移出视口；实测完整空输入框在 4 秒窗口之后才重现。
                // 仅给在线观察者看到过提交后重绘的情况延长回执时间，期间仍须取得清空证据。
                int confirmationSeconds = native == SubmissionStatus.Awaiting ? 12 : viewers.Count > 0 && revision > sent.BeforeSubmit &&
                    keyboardVersion == sent.KeyboardVersion ? 8 : 4;
                if (elapsed.TotalSeconds >= confirmationSeconds)
                {
                    draftMayContainText = true;
                    throw new IOException(native == SubmissionStatus.Awaiting ? "已发送回车，原生会话尚未记录本条消息，请核对终端" :
                        "已发送回车，未观察到原生输入行清空，请核对终端");
                }
                retry = !retried && elapsed.TotalMilliseconds >= 1100 && StillEditing(native);
            }
            if (retry)
            {
                await inputLock.WaitAsync(ct);
                try
                {
                    native = receipt?.Check(force: true) ?? SubmissionStatus.Unavailable;
                    if (native == SubmissionStatus.Confirmed) return;
                    lock (outputLock)
                    {
                        if (NativeQueuedSince(messageId, sent.BeforeSubmit)) return;
                        if (!StillEditing(native)) continue;
                        // 同一写锁内复核最新草稿与键盘版本，然后只补回车，不重写正文或再次认领。
                        retried = true;
                        review.BeforeSubmit = sent.BeforeSubmit = revision;
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
        // 首次回车可能既被吞掉又没有任何重绘。当前帧仍完整匹配时可复用该帧证据，等待原生会话回执。
        bool StillEditing(SubmissionStatus native) => Online && Volatile.Read(ref keyboardIntent) == sent.KeyboardIntent && keyboardVersion == sent.KeyboardVersion && revision > sent.BeforePaste &&
            (clearedDraftRevision <= sent.BeforePaste || clearedAtRevision <= sent.BeforeSubmit) &&
            !screens.Values.Any(s => s.Revision == revision && s.Screen == "blocked") &&
            (!(sent.Folded || lastFoldedRevision > sent.BeforePaste) || CompleteDraftStable(messageId, tail, expectedChars, sent.BeforePaste)) &&
            (Stopwatch.GetElapsedTime(lastActivity).TotalMilliseconds >= 250 || CompleteDraftStable(messageId, tail, expectedChars, sent.BeforePaste)) &&
            (((native == SubmissionStatus.Unavailable || sent.Folded || (codexEditor && CompleteDraftStable(messageId, tail, expectedChars, sent.BeforePaste))) && revision == sent.BeforeSubmit) || visibleDrafts.Any(pair => viewers.ContainsKey(pair.Key) &&
                pair.Value.Revision == revision &&
                (pair.Value.Message == messageId || pair.Value.Message == messageId[..8]) &&
                (!codexEditor || pair.Value.Tail == null || pair.Value.Tail == tail || pair.Value.FoldedChars == expectedChars) &&
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
                    ResetTypedLine();
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

            var start = WindowsCommand.Resolve(executable, arguments);
            var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in start.Environment)
                if (pair.Value != null) variables[pair.Key] = pair.Value;
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
