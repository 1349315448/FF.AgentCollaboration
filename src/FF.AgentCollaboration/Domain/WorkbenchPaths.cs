namespace FF.AgentCollaboration.Domain;

internal sealed record WorkbenchPaths(string? Archive_root, string? Handoffs, string? Data_dir = null);
internal sealed record InitializeWorkbench(string? Archive_root, string? Handoffs, string? Data_dir, string? NodeName);
