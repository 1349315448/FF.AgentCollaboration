using System.Text.RegularExpressions;

namespace FF.AgentCollaboration.Domain;

internal sealed record ManagedMcp
{
    public string? Id { get; init; }
    public string Name { get; init; } = "";
    public string Transport { get; init; } = "stdio";
    public string? Command { get; init; }
    public string[] Args { get; init; } = [];
    public string? Url { get; init; }
    public Dictionary<string,string?> Env { get; init; } = [];
    public Dictionary<string,string?> Headers { get; init; } = [];
    public string Scope { get; init; } = "global";
    public string? ProjectPath { get; init; }
    public string[] Engines { get; init; } = [];
    public bool Enabled { get; init; } = true;
    public string? UpdatedAt { get; init; }

    internal ManagedMcp Masked() => this with { Env = Hide(Env), Headers = Hide(Headers) };
    private static Dictionary<string,string?> Hide(Dictionary<string,string?> values) =>
        values.ToDictionary(k => k.Key, _ => (string?)null);

    internal bool Applies(string engine, string project) => Enabled &&
        (Engines.Length == 0 || Engines.Contains(engine)) &&
        (Scope == "global" || string.Equals(ProjectPath, project, StringComparison.OrdinalIgnoreCase));

    internal ManagedMcp Validate(ManagedMcp? prior)
    {
        if (Name is null || !Regex.IsMatch(Name, @"\A[a-zA-Z0-9_-]{1,64}\z") ||
            Name.Equals("lx_collab", StringComparison.OrdinalIgnoreCase) || Name.Equals("workbench-mcp", StringComparison.OrdinalIgnoreCase))
            throw new WorkbenchError("名称限 1 到 64 位字母、数字、短横线和下划线，且不能使用平台保留名");
        if (Scope is not ("global" or "project") || Scope == "project" && string.IsNullOrWhiteSpace(ProjectPath))
            throw new WorkbenchError("请选择有效的 MCP 作用范围和项目");
        if (Engines is null || Engines.Any(x => !EngineNames.Supports(x))) throw new WorkbenchError("存在未知引擎");
        if (Args is null || Args.Length > 128 || Args.Any(s => s is null || s.Length > 8192 || s.Contains('\0')))
            throw new WorkbenchError("MCP 参数列表无效");
        if (Env is null || Headers is null) throw new WorkbenchError("环境变量和请求头必须是键值对象");
        if (Args.Concat([Command,Url]).Concat(Env.Keys).Concat(Env.Values).Concat(Headers.Values).Any(v =>
            v?.Contains("FF_P0_WINDOW_GRANT",StringComparison.OrdinalIgnoreCase)==true ||
            v?.Contains("LX_P0_WINDOW_GRANT",StringComparison.OrdinalIgnoreCase)==true))
            throw new WorkbenchError("托管 MCP 不能引用窗口专属授权变量");
        if (Transport == "stdio")
        {
            if (string.IsNullOrWhiteSpace(Command) || Command.Length > 1024 || Command.Any(char.IsControl) ||
                !string.IsNullOrEmpty(Url) || Headers.Count != 0) throw new WorkbenchError("stdio 需要命令，可附参数和环境变量");
        }
        else if (Transport is not ("http" or "sse") || !Uri.TryCreate(Url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || Url!.Length > 4096 ||
            !string.IsNullOrEmpty(Command) || Args.Length != 0 || Env.Count != 0)
            throw new WorkbenchError("远程 MCP 需要 HTTP(S) 地址，可附请求头");
        return this with { Engines = Engines.Distinct().Order().ToArray(), Command = Command?.Trim(),
            Env = Resolve(Env, prior?.Env, false), Headers = Resolve(Headers, prior?.Headers, true) };
    }

    // null 保留同一条目同一字段的原值。客户端从来不需要读回真实令牌。
    private static Dictionary<string,string?> Resolve(Dictionary<string,string?> values, Dictionary<string,string?>? previous, bool header)
    {
        var result = new Dictionary<string,string?>(StringComparer.OrdinalIgnoreCase);
        if (values.Count > 128) throw new WorkbenchError("环境变量或请求头过多");
        foreach (var (key, input) in values)
        {
            if (!Regex.IsMatch(key, header ? @"\A[A-Za-z0-9!#$%&'*+.^_\x60|~-]{1,128}\z" : @"\A[A-Za-z_][A-Za-z0-9_]{0,127}\z"))
                throw new WorkbenchError("环境变量或请求头名称无效");
            var value = input ?? previous?.GetValueOrDefault(key);
            if (value is null || value.Length > 16384 || value.Contains('\0') || header && value.Any(char.IsControl) ||
                !result.TryAdd(key,value)) throw new WorkbenchError("环境变量或请求头值缺失、重复或无效");
        }
        return result;
    }
}
