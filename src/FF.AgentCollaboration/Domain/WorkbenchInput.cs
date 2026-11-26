namespace FF.AgentCollaboration.Domain;

internal static class WorkbenchInput
{
    internal static string Text(string? value, string label, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new WorkbenchError($"请填写{label}");
        if (value.Length > maxLength) throw new WorkbenchError($"{label}超过允许长度");
        return value.Trim();
    }

    internal static string Id(string? id) => Guid.TryParseExact(id, "N", out var parsed)
        ? parsed.ToString("N") : throw new WorkbenchError("编号无效");

    internal static string? AgentRole(string? value)
    {
        if (value == null) return null;
        var clean = string.Concat(value.Where(c => !char.IsControl(c))).Trim();
        if (clean.Length > 200) throw new WorkbenchError("身份描述最多 200 个字符");
        return clean.Length == 0 ? null : clean;
    }

    internal static string AgentName(string? value)
    {
        string name = Text(value, "Agent 名称", 40).TrimStart('@').Trim();
        if (name.Length == 0 || name.Any(char.IsControl)) throw new WorkbenchError("请填写有效的 Agent 名称");
        return name;
    }
}
