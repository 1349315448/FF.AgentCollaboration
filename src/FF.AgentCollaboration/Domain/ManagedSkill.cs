using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace FF.AgentCollaboration.Domain;

internal sealed record ManagedSkill(string Id, string Name, string Description, string Scope, string? ProjectPath,
    string[] Engines, bool Enabled, string Revision, bool Deleted);
internal sealed record SkillFileInput(string Path, byte[] Data);
internal sealed record SkillEdit(string? Content, string Scope="global", string? ProjectPath=null, string[]? Engines=null,
    bool Enabled=true, string? Revision=null, SkillFileInput[]? Files=null, string? Name=null, string? Description=null);
internal sealed record SkillTarget(string Engine,string Project,string Name);
internal sealed record SkillResult(ManagedSkill? Skill, List<string> Issues);
internal sealed record NativeSkillRemoval(string Id,string Path,string Revision,Dictionary<string,string> Files,
    string[] Directories,bool Moved);

internal static class SkillMarkdown
{
    internal const int DocumentLimit = 512 * 1024;
    internal static readonly UTF8Encoding Utf8 = new(false, true);

    internal static string Template(string name,string description) =>
        "---\nname: "+JsonSerializer.Serialize(name)+"\ndescription: "+JsonSerializer.Serialize(description)+"\n---\n\n# "+name+"\n";

    internal static (string Name,string Description) Parse(string text)
    {
        if(Utf8.GetByteCount(text)>DocumentLimit || text.Contains('\0')) throw new WorkbenchError("SKILL.md 需要是 512 KiB 以内的 UTF-8 文本");
        using var reader=new StringReader(text.TrimStart('\uFEFF'));
        if(reader.ReadLine()?.TrimEnd()!="---") throw new WorkbenchError("SKILL.md 需要以 YAML 元数据开头");
        var header=new StringBuilder();
        while(reader.ReadLine() is { } line && header.Length<=32768)
        {
            if(line.TrimEnd()!="---") { header.AppendLine(line); continue; }
            try
            {
                var yaml=new YamlStream();
                yaml.Load(new StringReader(header.ToString()));
                if(yaml.Documents.Count!=1 || yaml.Documents[0].RootNode is not YamlMappingNode map) break;
                string? Field(string key)=>map.Children.TryGetValue(new YamlScalarNode(key),out var value) ? (value as YamlScalarNode)?.Value : null;
                string name=Field("name")??"", description=Field("description")?.Trim()??"";
                if(!Regex.IsMatch(name,@"\A[a-z0-9]+(?:-[a-z0-9]+)*\z") || name.Length>64 || name=="ff-agent-collab" ||
                    Regex.IsMatch(name,@"\A(?:con|prn|nul|aux|com[0-9]|lpt[0-9])\z") || description.Length is 0 or >1024)
                    throw new WorkbenchError("请填写有效的技能名及说明。名称限 64 位小写字母、数字和短横线，平台协作技能名称保留");
                return (name,description);
            }
            catch(YamlException) { break; }
        }
        throw new WorkbenchError("请检查 SKILL.md 的 YAML 元数据、name 与 description");
    }
}
