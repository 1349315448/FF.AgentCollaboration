namespace FF.AgentCollaboration.Domain;

internal sealed record WorkbenchPaths(string? Archive_root, string? Handoffs, string? Data_dir = null);
