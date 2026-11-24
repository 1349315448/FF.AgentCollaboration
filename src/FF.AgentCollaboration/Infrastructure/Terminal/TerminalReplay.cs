using System.Text;

namespace FF.AgentCollaboration.Infrastructure.Terminal;

// 被滚动窗口裁掉的 VT 输出仍可能包含会持续生效的控制台模式。
internal sealed class TerminalReplay
{
    private static readonly HashSet<int> Modes = [1, 6, 7, 25, 1000, 1002, 1003, 1004, 1005, 1006, 1049, 2004];
    private readonly Dictionary<int, bool> applied = new();
    private readonly byte[] sequence = new byte[72];
    private int length;
    private bool? applicationKeypad;

    internal void RemovePrefix(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value == 27)
            {
                sequence[0] = value;
                length = 1;
                continue;
            }
            if (length == 0) continue;
            if (length == 1 && value != '[')
            {
                if (value == '=') applicationKeypad = true;
                else if (value == '>') applicationKeypad = false;
                else if (value == 'c') { applied.Clear(); applicationKeypad = null; }
                length = 0;
                continue;
            }
            if (length == sequence.Length) { length = 0; continue; }
            sequence[length++] = value;
            if (length <= 2 || value < 0x40 || value > 0x7e) continue;
            if (value == 'p' && length == 4 && sequence[2] == '!')
            {
                foreach (int mode in new[] { 1, 6, 7, 25, 1004, 2004 }) applied.Remove(mode);
                applicationKeypad = null;
            }
            else if (value is (byte)'h' or (byte)'l' && sequence[2] == '?')
            {
                foreach (string part in Encoding.ASCII.GetString(sequence, 3, length - 4).Split(';'))
                {
                    if (!int.TryParse(part, out int mode)) continue;
                    if (mode is 47 or 1047) mode = 1049;
                    if (Modes.Contains(mode)) applied[mode] = value == 'h';
                }
            }
            length = 0;
        }
    }

    internal byte[] RestoreModes()
    {
        var result = new StringBuilder();
        // 先选缓冲区，再恢复其余模式和被裁断的 CSI 前缀。
        foreach (var (mode, enabled) in applied.OrderBy(pair => pair.Key == 1049 ? 0 : 1))
            result.Append("\u001b[?").Append(mode).Append(enabled ? 'h' : 'l');
        if (applicationKeypad.HasValue) result.Append(applicationKeypad.Value ? "\u001b=" : "\u001b>");
        result.Append(Encoding.ASCII.GetString(sequence, 0, length));
        return Encoding.ASCII.GetBytes(result.ToString());
    }
}
