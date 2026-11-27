using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Infrastructure.Terminal;

internal sealed partial class TerminalSession
{
    // Only a fingerprint of printable input from an observed empty editor. Cursor movement,
    // shortcuts or editing invalidate it; screen text alone is insufficient authority to clear.
    private IncrementalHash? typedLine;
    private int typedBytes;
    private long typedIntent;
    private readonly HashSet<int> composingViewers = new();
    private (string Message, long Intent, long Epoch, byte[] Hash)? deferredDraft;

    internal async Task<bool> RestoreAfterDelivery(string message, ManualEditorDraft draft, CancellationToken ct)
    {
        if (draft.Generation != Generation || draft.Text is not { Length: > 0 and <= 4096 }) return false;
        await deliveryLock.WaitAsync(ct);
        try
        {
            long intent;
            lock (outputLock)
            {
                if (deferredDraft is not {} saved || saved.Message != message || saved.Epoch != deliveryEpoch ||
                    !BlankEditor(saved.Intent, -1) || !saved.Hash.AsSpan().SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(draft.Text)))) return false;
                deferredDraft = null;
                intent = saved.Intent;
            }
            return await RestoreManualDraft(draft.Text, intent, ct);
        }
        finally { deliveryLock.Release(); }
    }

    internal void ObserveComposition(int viewer, bool active)
    {
        lock (outputLock)
        {
            if (active) composingViewers.Add(viewer);
            else composingViewers.Remove(viewer);
        }
    }

    private void ResetTypedLine()
    {
        typedLine?.Dispose();
        typedLine = null;
        typedBytes = 0;
    }

    private void TrackTypedLine(ReadOnlySpan<byte> data, long intent)
    {
        if (Enqueue != null || NativeInbox) return;
        if (ready.Values.Contains(revision) && !draftMayContainText) ResetTypedLine();
        if (data.StartsWith("\u001b[200~"u8) && data.EndsWith("\u001b[201~"u8) && data.Length >= 12)
            data = data[6..^6];
        if (data.IsEmpty || data.Length > 4096 - typedBytes || data.ContainsAnyInRange((byte)0, (byte)31) || data.Contains((byte)127))
        {
            ResetTypedLine();
            return;
        }
        if (typedLine == null)
        {
            if (!ready.Values.Contains(revision) || draftMayContainText || mentionPickers.Count != 0) return;
            typedLine = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        }
        typedLine.AppendData(data);
        typedBytes += data.Length;
        typedIntent = intent;
    }

    private async Task<long?> ClearManualDraft(ManualEditorDraft draft, CancellationToken ct)
    {
        if (draft.Generation != Generation || draft.Text is not {Length: > 0 and <= 4096} || draft.Text.Any(char.IsControl)) return null;
        byte[] text = Encoding.UTF8.GetBytes(draft.Text);
        long intent;
        await inputLock.WaitAsync(ct);
        try
        {
            lock (outputLock)
            {
                intent = Volatile.Read(ref keyboardIntent);
                if (!Online || revision != draft.Revision || !bracketedPasteEnabled || pasteRevision != revision ||
                    mentionPickers.Count != 0 || composingViewers.Count != 0 || typedIntent != intent || typedBytes != text.Length || typedLine == null ||
                    !SHA256.HashData(text).AsSpan().SequenceEqual(typedLine.GetCurrentHash()) ||
                    !screens.Values.Any(s => s.Revision == revision && s.Screen == "draft") ||
                    screens.Values.Any(s => s.Revision == revision && (s.Screen != "draft" || s.Draft != null))) return null;
                ResetTypedLine();
                MarkInput();
                input.WriteByte(21); // Ctrl+U at end of a verified single-line editor.
                input.Flush();
            }
        }
        finally { inputLock.Release(); }
        return await WaitForBlankEditor(intent, draft.Revision, ct) ? intent : null;
    }

    private bool BlankEditor(long intent, long after) =>
        Volatile.Read(ref keyboardIntent) == intent && revision > after && CanDeliver(true) &&
        !screens.Values.Any(s => s.Revision == revision && !s.Idle);

    private async Task<bool> WaitForBlankEditor(long intent, long after, CancellationToken ct)
    {
        long start = Stopwatch.GetTimestamp();
        do
        {
            lock (outputLock)
            {
                if (!Online || intent != Volatile.Read(ref keyboardIntent)) return false;
                if (BlankEditor(intent, after)) return true;
            }
            await Task.Delay(80, ct);
        } while (Stopwatch.GetElapsedTime(start).TotalSeconds < 3);
        return false;
    }

    private async Task<bool> RestoreManualDraft(string text, long intent, CancellationToken ct)
    {
        // The delivery already has a receipt. A late disconnect must only affect draft recovery.
        try
        {
            if (!await WaitForBlankEditor(intent, -1, ct)) return false;
            await inputLock.WaitAsync(ct);
            try
            {
                lock (outputLock)
                {
                    if (!BlankEditor(intent, -1)) return false;
                    byte[] body = Encoding.UTF8.GetBytes(text);
                    TrackTypedLine(body, intent);
                    draftMayContainText = true;
                    MarkInput();
                    byte[] paste = Encoding.UTF8.GetBytes("\u001b[200~" + text + "\u001b[201~");
                    input.Write(paste);
                    input.Flush();
                    return true;
                }
            }
            finally { inputLock.Release(); }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { return false; }
    }
}
