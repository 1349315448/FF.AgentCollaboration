using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FF.AgentCollaboration.Domain;
using Microsoft.Win32.SafeHandles;

namespace FF.AgentCollaboration.Infrastructure.Files;

internal sealed class JsonlSubmissionReceipts(Func<string?> locate, Func<JsonElement, string, bool> matches,
    Func<JsonElement, bool>? sessionHeader = null) : ISubmissionReceiptSource
{
    public ISubmissionReceipt Begin(string messageId, string envelope)
    {
        try
        {
            string? path = locate();
            return new Cursor(path, messageId, envelope, matches, sessionHeader);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new Cursor(null, messageId, envelope, matches, sessionHeader);
        }
    }

    // The window delivery lock owns each cursor. Follow appends from the baseline and retain partial UTF-8 records.
    private sealed class Cursor : ISubmissionReceipt
    {
        private readonly string? path;
        private readonly Func<JsonElement, string, bool> matches;
        private readonly Func<JsonElement, bool>? sessionHeader;
        private readonly string envelope;
        private readonly byte[] marker = new byte[64];
        private readonly byte[] buffer = new byte[64 * 1024];
        private readonly MemoryStream partial = new();
        private long offset;
        private int markerLength;
        private bool skipLine;
        private long lastPoll;
        private SubmissionStatus status;
        private bool headerVerified;
        private FileIdentity? identity;

        internal Cursor(string? path, string messageId, string envelope, Func<JsonElement, string, bool> matches,
            Func<JsonElement, bool>? sessionHeader)
        {
            this.path = path;
            this.matches = matches;
            this.sessionHeader = sessionHeader;
            this.envelope = envelope.ReplaceLineEndings("\n");
            if (messageId.Length < 8 || !this.envelope.StartsWith($"MSG_ID: {messageId[..8]} | ", StringComparison.Ordinal))
                this.path = null;
            if (this.path == null) return;
            try
            {
                using var file = Open();
                identity = GetIdentity(file);
                headerVerified = VerifyHeader(file);
                offset = file.Length;
                RememberBoundary(file);
                skipLine = markerLength != 0 && marker[markerLength - 1] != (byte)'\n';
                if (headerVerified) status = SubmissionStatus.Awaiting;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        private FileStream Open() => new(path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        [StructLayout(LayoutKind.Sequential)]
        private readonly record struct FileIdentity(ulong Volume, Guid File);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileIdentity value, uint size);
        private static FileIdentity? GetIdentity(FileStream file) => OperatingSystem.IsWindows() &&
            GetFileInformationByHandleEx(file.SafeFileHandle, 18, out var id, 24) ? id : null;

        private bool VerifyHeader(FileStream file)
        {
            if (sessionHeader == null) return true;
            file.Position = 0;
            using var line = new MemoryStream();
            while (line.Length < 512 * 1024)
            {
                int read = file.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                int end = Array.IndexOf(buffer, (byte)'\n', 0, read);
                line.Write(buffer, 0, end >= 0 ? end : read);
                if (end < 0) continue;
                try
                {
                    using var json = JsonDocument.Parse(line.GetBuffer().AsMemory(0, (int)line.Length));
                    return json.RootElement.ValueKind == JsonValueKind.Object && sessionHeader(json.RootElement);
                }
                catch (JsonException) { return false; }
            }
            return false;
        }

        public SubmissionStatus Check(bool force = false)
        {
            if (path == null || status == SubmissionStatus.Confirmed ||
                (!force && Stopwatch.GetElapsedTime(lastPoll).TotalMilliseconds < 250)) return status;
            lastPoll = Stopwatch.GetTimestamp();
            try
            {
                using var file = Open();
                var fileId = GetIdentity(file);
                Span<byte> previous = stackalloc byte[64];
                if (offset >= markerLength) file.Position = offset - markerLength;
                // Reopen the pathname to follow rename/replace. Verify the previous bytes as writers can also truncate in place.
                if (fileId != identity || file.Length < offset || file.Read(previous[..markerLength]) != markerLength ||
                    !previous[..markerLength].SequenceEqual(marker.AsSpan(0, markerLength)))
                {
                    offset = 0;
                    partial.SetLength(0);
                    skipLine = false;
                    headerVerified = false;
                }
                identity = fileId;
                if (!headerVerified && !(headerVerified = VerifyHeader(file))) return status;
                file.Position = offset;
                status = SubmissionStatus.Awaiting;
                long end = Math.Min(file.Length, offset + 256 * 1024);
                while (offset < end)
                {
                    int count = file.Read(buffer, 0, (int)Math.Min(buffer.Length, end - offset));
                    if (count == 0) break;
                    offset += count;
                    if (Consume(buffer.AsSpan(0, count))) return status = SubmissionStatus.Confirmed;
                }
                RememberBoundary(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Retry at the next poll. */ }
            return status;
        }

        private void RememberBoundary(FileStream file)
        {
            markerLength = (int)Math.Min(marker.Length, offset);
            file.Position = offset - markerLength;
            file.ReadExactly(marker.AsSpan(0, markerLength));
        }

        private bool Consume(ReadOnlySpan<byte> bytes)
        {
            while (!bytes.IsEmpty)
            {
                int newline = bytes.IndexOf((byte)'\n');
                var fragment = newline < 0 ? bytes : bytes[..newline];
                if (partial.Length + fragment.Length > 128 * 1024) skipLine = true;
                if (!skipLine) partial.Write(fragment);
                if (newline < 0) break;
                if (!skipLine)
                {
                    var line = partial.GetBuffer().AsMemory(0, (int)partial.Length);
                    try
                    {
                        using var json = JsonDocument.Parse(line);
                        if (json.RootElement.ValueKind == JsonValueKind.Object && matches(json.RootElement, envelope)) return true;
                    }
                    catch (JsonException) { }
                }
                partial.SetLength(0);
                skipLine = false;
                bytes = bytes[(newline + 1)..];
            }
            return false;
        }
    }

    internal static bool StringIs(JsonElement item, string key, string expected) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.ValueEquals(expected);

    internal static bool IsUserEnvelope(JsonElement message, string envelope)
    {
        if (!StringIs(message, "role", "user") || !message.TryGetProperty("content", out var parts) ||
            parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() == 0) return false;
        var text = new System.Text.StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object || !StringIs(part, "type", "input_text") ||
                !part.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String) return false;
            text.Append(value.GetString());
        }
        return text.ToString().ReplaceLineEndings("\n").Equals(envelope, StringComparison.Ordinal);
    }
}
