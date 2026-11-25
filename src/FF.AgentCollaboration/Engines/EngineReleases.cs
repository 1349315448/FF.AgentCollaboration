using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FF.AgentCollaboration.Engines;

// 只查询官方稳定发行渠道。ZCode 软件包与仓库发布标签各自独立编号。
internal static class EngineReleases
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(8),
        DefaultRequestHeaders = { { "User-Agent", "FF-AgentCollaboration" } } };
    private static readonly ConcurrentDictionary<string, (string Version, DateTimeOffset Expires)> Cache = new();
    private static readonly Regex VersionNumber = new(@"(?<![\d.])(?<core>\d+\.\d+\.\d+)(?<pre>-[0-9A-Za-z.-]+)?", RegexOptions.CultureInvariant);

    internal static string? NpmPackage(string engine) => engine switch
    {
        "codex" => "@openai/codex", "copilot" => "@github/copilot", "claude" => "@anthropic-ai/claude-code",
        "codebuddy" => "@tencent-ai/codebuddy-code", "qwen" => "@qwen-code/qwen-code", _ => null
    };

    internal static async Task<string?> Latest(string engine, bool refresh, CancellationToken ct)
    {
        if (!refresh && Cache.TryGetValue(engine, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            return cached.Version;
        try
        {
            string url;
            if (engine == "zcode")
            {
                var release = await Client.GetFromJsonAsync<JsonElement>("https://api.github.com/repos/zai-org/ZCode/releases/latest", ct);
                string tag = release.GetProperty("tag_name").GetString()!;
                url = $"https://raw.githubusercontent.com/zai-org/ZCode/{Uri.EscapeDataString(tag)}/apps/zcode-cli/package.json";
            }
            else url = engine == "traecli" ? "https://lf-cdn.trae.com.cn/obj/trae-com-cn/trae-cli/v2/public/stable/latest.json" :
                $"https://registry.npmjs.org/{NpmPackage(engine)}/latest";
            var package = await Client.GetFromJsonAsync<JsonElement>(url, ct);
            var version = package.GetProperty("version").GetString();
            if (!Version.TryParse(version, out var stable) || stable.Build < 0 || stable.ToString() != version) return null;
            Cache[engine] = (version!, DateTimeOffset.UtcNow.AddMinutes(15));
            return version;
        }
        catch (Exception error) when (!ct.IsCancellationRequested && error is HttpRequestException or OperationCanceledException
            or JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    internal static string Compare(string? local, string? latest)
    {
        if (latest == null) return "unavailable";
        var match = VersionNumber.Match(local ?? "");
        if (!Version.TryParse(match.Groups["core"].Value, out var current)) return "unknown";
        int order = current.CompareTo(Version.Parse(latest));
        if (order < 0 || order == 0 && match.Groups["pre"].Success) return "available";
        return order > 0 ? "ahead" : "current";
    }
}
