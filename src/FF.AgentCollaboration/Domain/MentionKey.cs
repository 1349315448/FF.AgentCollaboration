using System.Text.RegularExpressions;

namespace FF.AgentCollaboration.Domain;

internal sealed record MentionKey(string? Chord)
{
    internal const string Default = "Alt+Shift+A";

    internal static string Validate(string? chord)
    {
        if (chord == null) return Default;
        if (chord == "Alt+Shift+F") throw new WorkbenchError("Alt+Shift+F 已用于选择文件");
        if (chord is "Ctrl+W" or "Ctrl+T" or "Ctrl+N" or "Ctrl+L" or "Ctrl+R" or "Ctrl+Shift+T"
            or "Ctrl+Shift+N" or "Ctrl+Shift+W" or "Ctrl+Shift+I" or "Ctrl+Shift+J" or "Ctrl+Shift+C")
            throw new WorkbenchError("该组合键已由浏览器使用，请换一个");
        if (chord.Length > 24 || !Regex.IsMatch(chord, @"\A(?=Ctrl\+|Alt\+)(?:Ctrl\+)?(?:Alt\+)?(?:Shift\+)?[A-Z0-9]\z"))
            throw new WorkbenchError("请使用 Ctrl 或 Alt 搭配字母或数字，可同时按 Shift");
        return chord;
    }
}
