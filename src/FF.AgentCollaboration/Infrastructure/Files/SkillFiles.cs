using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Infrastructure.Files;

// 只在已经解析的技能根下操作。先隔离旧目录再复核内容，绝不递归删除未验证的目录树。
internal static class SkillFiles
{
    internal const string Marker=".ff-managed";
    internal const int MaxFiles=256, MaxBytes=8*1024*1024;
    private static WorkbenchError Conflict(string message)=>new(message,WorkbenchFailure.Conflict);
    internal static string Child(string root,string path)
    {
        var parts=path.Replace('\\','/').Split('/');
        if(path.Length>512 || parts.Length>24 || parts.Any(p=>p.StartsWith('.') || p.Length>128))
            throw new WorkbenchError("技能资源路径不能包含隐藏项、上级目录或过深层级");
        return new ProjectFileBrowser(root).Resolve(string.Join('/',parts));
    }
    internal static void EnsureDirectory(string path)
    {
        TopicArchive.CheckPath(path);
        if(Directory.Exists(path)) return;
        var parent=Path.GetDirectoryName(path)??throw new WorkbenchError("技能根路径无效");
        EnsureDirectory(parent);
        using var guard=WorkspaceDirectoryGuard.Open(parent);
        guard.EnsureChild(path);
        TopicArchive.CheckPath(path);
    }
    internal static byte[] ReadFile(string path,long limit)
    {
        using var guard=WorkspaceDirectoryGuard.Open(Path.GetDirectoryName(path)!);
        TopicArchive.CheckPath(path);
        using var input=guard.Read(path);
        if(input.Length>limit) throw new WorkbenchError("技能包超过大小限制");
        var data=new byte[checked((int)input.Length)];
        input.ReadExactly(data);
        return data;
    }

    private sealed class Tree
    {
        internal readonly Dictionary<string,byte[]> Files=new(StringComparer.Ordinal);
        internal readonly List<string> Directories=[];
        internal int Bytes;
        internal byte[]? Markdown;
    }
    private static Tree Snapshot(string directory,bool native=false)
    {
        var result=new Tree();
        void Visit(string dir,int depth)
        {
            using var guard=WorkspaceDirectoryGuard.Open(dir);
            if(depth>(native?128:24)) throw new WorkbenchError("技能目录过深");
            foreach(string entry in Directory.EnumerateFileSystemEntries(dir))
            {
                TopicArchive.CheckPath(entry);
                if(!native && result.Files.Count+result.Directories.Count>MaxFiles*2) throw new WorkbenchError("技能包文件数量超限");
                string relative=Path.GetRelativePath(directory,entry).Replace('\\','/');
                if(File.GetAttributes(entry).HasFlag(FileAttributes.Directory))
                {
                    Visit(entry,depth+1);
                    result.Directories.Add(relative);
                }
                else if(native)
                {
                    using var stream=guard.Read(entry);
                    if(relative.Equals("SKILL.md",StringComparison.OrdinalIgnoreCase))
                    {
                        if(stream.Length>SkillMarkdown.DocumentLimit) throw new WorkbenchError("原生 SKILL.md 超过预览上限");
                        result.Markdown=new byte[(int)stream.Length];
                        stream.ReadExactly(result.Markdown);
                        result.Files.Add(relative,SHA256.HashData(result.Markdown));
                    }
                    else result.Files.Add(relative,SHA256.HashData(stream));
                }
                else
                {
                    var data=ReadFile(entry,MaxBytes+1024*1024-result.Bytes);
                    result.Bytes+=data.Length;
                    result.Files.Add(relative,data);
                }
            }
        }
        Visit(directory,0);
        return result;
    }

    private static string Digest(Dictionary<string,byte[]> files,IEnumerable<string> directories)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach(string dir in directories.Order(StringComparer.Ordinal)) hash.AppendData(Encoding.UTF8.GetBytes("d"+dir+"\0"));
        foreach(var (name,data) in files.OrderBy(x=>x.Key,StringComparer.Ordinal))
        {
            if(name==Marker) continue;
            hash.AppendData(Encoding.UTF8.GetBytes("f"+name+"\0"));
            hash.AppendData(SHA256.HashData(data));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static HashSet<string> ParentNames(IEnumerable<string> names)
    {
        var dirs=new HashSet<string>();
        foreach(string name in names)
            for(int i=name.IndexOf('/');i>=0;i=name.IndexOf('/',i+1)) dirs.Add(name[..i]);
        return dirs;
    }
    private sealed record Manifest(string Owner,Dictionary<string,string> Hashes);
    private static string Ownership(Tree tree,string owner,bool partial=false,bool staging=false)
    {
        if(!tree.Files.TryGetValue(Marker,out var bytes)) throw Conflict("同名原生目录已保留");
        Manifest? manifest;
        try {manifest=JsonSerializer.Deserialize<Manifest>(bytes);} catch(JsonException){manifest=null;}
        if(manifest?.Owner!=owner || manifest.Hashes==null) throw Conflict("标记不属于此发布位置，目录已保留");
        var dirs=ParentNames(manifest.Hashes.Keys);
        if(tree.Directories.Any(d=>!dirs.Contains(d)) || !partial && !dirs.SetEquals(tree.Directories))
            throw Conflict("副本目录结构已被修改，目录已保留");
        foreach(var (path,data) in tree.Files)
        {
            if(path==Marker || staging && path==".ff-writing") continue;
            if(!manifest.Hashes.TryGetValue(path,out var hash) || Convert.ToHexString(SHA256.HashData(data))!=hash)
                throw Conflict("副本文件已被修改，目录已保留");
        }
        if(!partial && tree.Files.Count!=manifest.Hashes.Count+1) throw Conflict("副本内容已变更，目录已保留");
        return Digest(tree.Files,tree.Directories);
    }
    private static void Erase(string dir,Tree verified)
    {
        // 仅移除刚刚核对的名称。子目录若在核验后增加文件，非递归删除会保留它。
        using(var guard=WorkspaceDirectoryGuard.Open(dir))
        {
            foreach(var name in verified.Files.Keys.Where(k=>k!=Marker))
            {
                string file=Path.Combine(dir,name);
                using var parent=WorkspaceDirectoryGuard.Open(Path.GetDirectoryName(file)!);
                TopicArchive.CheckPath(file);
                parent.Delete(file);
            }
            foreach(string name in verified.Directories)
            {
                string child=Path.Combine(dir,name);
                using var parent=WorkspaceDirectoryGuard.Open(Path.GetDirectoryName(child)!);
                TopicArchive.CheckPath(child);
                Directory.Delete(child);
            }
            if(verified.Files.ContainsKey(Marker)) guard.Delete(Path.Combine(dir,Marker));
        }
        Directory.Delete(dir);
    }
    internal static string NativeVersion(string directory)
    {
        var tree=Snapshot(directory,native:true);
        if(tree.Files.Keys.Any(k=>k.Equals(Marker,StringComparison.OrdinalIgnoreCase))) throw Conflict("此目录带有托管标记");
        return Digest(tree.Files,tree.Directories);
    }
    internal static (string Content,string Revision) NativePreview(string directory)
    {
        var tree=Snapshot(directory,native:true);
        if(tree.Files.Keys.Any(k=>k.Equals(Marker,StringComparison.OrdinalIgnoreCase))) throw Conflict("此目录带有托管标记");
        if(tree.Markdown==null) throw Conflict("不能预览该文件");
        try {return (SkillMarkdown.Utf8.GetString(tree.Markdown),Digest(tree.Files,tree.Directories));}
        catch(DecoderFallbackException){throw new WorkbenchError("原生 SKILL.md 不是有效的 UTF-8 文本");}
    }
    internal static NativeSkillRemoval PlanNativeRemoval(string directory,string expected)
    {
        var tree=Snapshot(directory,native:true);
        if(tree.Files.Keys.Contains(Marker,StringComparer.OrdinalIgnoreCase) || Digest(tree.Files,tree.Directories)!=expected)
            throw Conflict("原生技能已变更，请重新查看并确认");
        return new(Guid.NewGuid().ToString("N"),directory,expected,tree.Files.ToDictionary(p=>p.Key,p=>Convert.ToHexString(p.Value)),
            tree.Directories.ToArray(),false);
    }

    internal static void ResumeNativeRemoval(NativeSkillRemoval receipt,Action moved)
    {
        string parent=Path.GetDirectoryName(receipt.Path)!;
        using var guard=WorkspaceDirectoryGuard.Open(parent);
        string retired=Path.Combine(parent,".ff-retired-"+receipt.Id);
        TopicArchive.CheckPath(retired);
        if(!Directory.Exists(retired))
        {
            if(receipt.Moved) return; // 清理已完成，不能再处理同名新建的原生目录。
            if(NativeVersion(receipt.Path)!=receipt.Revision) throw Conflict("原生 Skill 已变更，请重新确认删除");
            Directory.Move(receipt.Path,retired);
        }
        var snapshot=Snapshot(retired,native:true);
        if(!receipt.Moved)
        {
            if(snapshot.Files.Keys.Contains(Marker,StringComparer.OrdinalIgnoreCase) || Digest(snapshot.Files,snapshot.Directories)!=receipt.Revision)
            {
                if(!Directory.Exists(receipt.Path)) Directory.Move(retired,receipt.Path);
                throw Conflict("原生 Skill 已变更，未执行删除");
            }
            moved();
        }
        if(snapshot.Directories.Except(receipt.Directories,StringComparer.Ordinal).Any() ||
            snapshot.Files.Any(p=>!receipt.Files.TryGetValue(p.Key,out var hash) || hash!=Convert.ToHexString(p.Value)))
            throw Conflict("隔离目录中出现未经确认的文件，已保留："+retired);
        Erase(retired,snapshot);
    }

    internal static void Apply(string root,string name,string owner,Dictionary<string,byte[]>? package)
    {
        string dest=Child(root,name);
        TopicArchive.CheckPath(dest);
        if(package==null && !Directory.Exists(root)) return;
        EnsureDirectory(root);
        using var guard=WorkspaceDirectoryGuard.Open(root);
        string backup=Path.Combine(root,".ff-old-"+owner+"-"+name);
        TopicArchive.CheckPath(backup);
        if(Directory.Exists(backup))
        {
            var old=Snapshot(backup);
            bool restore=false;
            if(old.Files.Count>0 || old.Directories.Count>0)
            {
                Ownership(old,owner,partial:true);
                if(!Directory.Exists(dest) && package!=null)
                {
                    try {Ownership(old,owner);restore=true;}
                    catch(WorkbenchError){ /* 已验证归属的部分清理残留，可从 SQLite 源包恢复。 */ }
                }
            }
            if(restore) Directory.Move(backup,dest); else Erase(backup,old);
        }
        bool exists=Directory.Exists(dest);
        string? desired=package==null?null:Digest(package,ParentNames(package.Keys));
        string stage=Path.Combine(root,".ff-new-"+owner+"-"+name);
        TopicArchive.CheckPath(stage);
        if(Directory.Exists(stage))
        {
            var old=Snapshot(stage);
            bool preparing=old.Directories.Count==0 && old.Files.Keys.All(k=>k==".ff-writing");
            if(!preparing) Ownership(old,owner,partial:true,staging:true);
            Erase(stage,old);
        }
        if(exists && !File.Exists(Path.Combine(dest,Marker))) throw Conflict("同名原生目录已保留");
        if(exists && Ownership(Snapshot(dest),owner)==desired) return;
        if(!exists && package==null) return;
        if(package!=null)
        {
            guard.EnsureChild(stage);
            using var stageGuard=WorkspaceDirectoryGuard.Open(stage);
            var manifest=new Manifest(owner,package.ToDictionary(p=>p.Key,p=>Convert.ToHexString(SHA256.HashData(p.Value))));
            // 先落归属清单，再逐文件发布。中断的临时字节只留在此回执专属的 .ff-writing。
            string marker=Path.Combine(stage,Marker);
            string pending=Path.Combine(stage,".ff-writing");
            using(var output=stageGuard.Create(pending))
            {
                JsonSerializer.Serialize(output,manifest);
                output.Flush(true);
            }
            stageGuard.Publish(pending,marker,replace:false);
            foreach(var (relative,data) in package)
            {
                string file=Child(stage,relative);
                EnsureDirectory(Path.GetDirectoryName(file)!);
                using var parent=WorkspaceDirectoryGuard.Open(Path.GetDirectoryName(file)!);
                using(var output=stageGuard.Create(pending)) output.Write(data);
                stageGuard.PublishTo(pending,parent,file);
            }
        }
        if(exists) Directory.Move(dest,backup);
        Tree? prior=null;
        try
        {
            if(exists) {prior=Snapshot(backup);Ownership(prior,owner);}
            if(package!=null) Directory.Move(stage,dest);
        }
        catch {if(exists && !Directory.Exists(dest)) Directory.Move(backup,dest);throw;}
        if(prior!=null) Erase(backup,prior);
    }
}
