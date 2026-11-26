using FF.AgentCollaboration.Domain;
using FF.AgentCollaboration.Engines;
using FF.AgentCollaboration.Infrastructure.Files;
using FF.AgentCollaboration.Infrastructure.Storage;

namespace FF.AgentCollaboration.Application;

internal sealed class ManagedSkills(string data)
{
    private static readonly object Gate=new();

    private static SkillTarget[] Targets(ManagedSkill s)
    {
        if(s.Deleted) return [];
        var library=new SkillTarget("library","",s.Id);
        if(!s.Enabled) return [library];
        return [library,..(s.Engines.Length==0?EngineNames.All:s.Engines)
            .Select(e=>new SkillTarget(e,s.ProjectPath??"",s.Name))];
    }

    internal object Catalog(string? project)
    {
        lock(Gate)
        {
            using var db=new WorkbenchStore(data);
            var (native,issues)=EngineSkillRoots.Inventory(project==null?null:db.RegisteredProject(project));
            var all=db.Skills(true);
            var nativeCleanup=db.NativeSkillRemovals().Select(r=>{
                string retired=Path.Combine(Path.GetDirectoryName(r.Path)!,".ff-retired-"+r.Id);
                return new {r.Id,r.Path,currentPath=Directory.Exists(retired)?retired:r.Path};});
            return new {managed=all.Where(s=>!s.Deleted),pendingCleanup=all.Where(s=>s.Deleted),nativeCleanup,native,issues,engines=EngineNames.All};
        }
    }
    internal object Detail(string id)
    {
        lock(Gate)
        {
            using var db=new WorkbenchStore(data);
            var skill=db.Skill(id);
            var package=db.SkillPackage(id);
            return new {skill,content=SkillMarkdown.Utf8.GetString(package["SKILL.md"]),files=package.Keys.Order()};
        }
    }

    private Dictionary<string,byte[]> Import(SkillEdit input)
    {
        if(input.Files==null || input.Files.Length is 0 or >SkillFiles.MaxFiles) throw new WorkbenchError("请选择 Markdown 文件或包含 SKILL.md 的技能目录");
        var package=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
        var directories=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        long bytes=0;
        foreach(var f in input.Files)
        {
            if(f?.Path==null || f.Data==null) throw new WorkbenchError("缺少文件名或内容");
            string name=f.Path.Replace('\\','/');
            SkillFiles.Child(data,name);
            bytes+=f.Data.Length;
            if(bytes>SkillFiles.MaxBytes || !package.TryAdd(name,f.Data)) throw new WorkbenchError("技能包超过 8 MiB 或包含同名文件");
            for(int i=name.IndexOf('/');i>=0;i=name.IndexOf('/',i+1))
            {
                var dir=name[..i];
                if(directories.TryGetValue(dir,out var spelling) && spelling!=dir) throw new WorkbenchError("资源目录的大小写拼写不一致");
                directories[dir]=dir;
            }
        }
        if(package.Keys.Any(directories.ContainsKey)) throw new WorkbenchError("技能包中存在同名文件和目录");
        if(package.Count+directories.Count+1>SkillFiles.MaxFiles*2) throw new WorkbenchError("技能包中的目录和文件过多");
        if(!package.Keys.Contains("SKILL.md",StringComparer.Ordinal)) throw new WorkbenchError("技能根目录需要 SKILL.md");
        return package;
    }

    internal SkillResult Save(string? id,SkillEdit input,bool import=false)
    {
        lock(Gate)
        {
            using var db=new WorkbenchStore(data);
            var old=id==null?null:db.Skill(id);
            if(old!=null && old.Revision!=input.Revision) throw new WorkbenchError("Skill 已变更，请重新加载",WorkbenchFailure.Conflict);
            var package=import?Import(input):old!=null?db.SkillPackage(old.Id):new Dictionary<string,byte[]>();
            if(input.Scope is not ("project" or "global") || input.Engines?.Any(e=>!EngineNames.Supports(e))==true)
                throw new WorkbenchError("未知范围或引擎");
            string? project=input.Scope=="project"?db.RegisteredProject(input.ProjectPath??""):null;
            string content;
            if(import)
            {
                try {content=SkillMarkdown.Utf8.GetString(package["SKILL.md"]);}
                catch(System.Text.DecoderFallbackException) {throw new WorkbenchError("SKILL.md 需要使用 UTF-8 编码");}
                if(!content.TrimStart('\uFEFF').StartsWith("---",StringComparison.Ordinal) && input.Name!=null)
                    content=SkillMarkdown.Template(input.Name,input.Description??"")+"\n"+content;
            }
            else content=input.Content??throw new WorkbenchError("缺少 SKILL.md 正文");
            var (name,description)=SkillMarkdown.Parse(content);
            package["SKILL.md"]=SkillMarkdown.Utf8.GetBytes(content);
            if(package.Values.Sum(v=>(long)v.Length)>SkillFiles.MaxBytes) throw new WorkbenchError("技能包超过 8 MiB");
            var skill=new ManagedSkill(id??Guid.NewGuid().ToString("N"),name,description,input.Scope,project,
                (input.Engines??[]).Distinct().Order().ToArray(),input.Enabled,Guid.NewGuid().ToString("N"),false);
            db.SaveSkill(skill,package,old?.Revision);
            return Reconcile(db,skill,package);
        }
    }
    internal SkillResult Delete(string id,string revision)
    {
        lock(Gate)
        {
            using var db=new WorkbenchStore(data);
            var s=db.Skill(id);
            db.RetireSkill(id,revision);
            return Reconcile(db,s with {Deleted=true},[]);
        }
    }
    internal SkillResult Sync(string id)
    {
        lock(Gate)
        {
            using var db=new WorkbenchStore(data);
            var s=db.Skills(true).Find(s=>s.Id==id)??throw new WorkbenchError("技能已移除",WorkbenchFailure.NotFound);
            return Reconcile(db,s,s.Deleted?[]:db.SkillPackage(id));
        }
    }
    internal void Recover()
    {
        lock(Gate)
        {
            using var db=new WorkbenchStore(data);
            foreach(var s in db.Skills(true)) Reconcile(db,s,s.Deleted?[]:db.SkillPackage(s.Id));
            foreach(var r in db.NativeSkillRemovals())
            {
                try {ResumeNative(db,r);} catch(Exception e) when(e is WorkbenchError or IOException or UnauthorizedAccessException)
                { /* 保留收据，抽屉可查看并重试。 */ }
            }
        }
    }
    // 提交期望状态和目标清单后再发布。更新被拒的目录留给用户，进程中断后可按清单继续。
    private SkillResult Reconcile(WorkbenchStore db,ManagedSkill s,Dictionary<string,byte[]> package)
    {
        List<string> issues=[];
        var desired=new Dictionary<SkillTarget,string>();
        var tracked=db.SkillTargets(s.Id);
        foreach(var t in Targets(s))
        {
            try
            {
                string path=EngineSkillRoots.Destination(data,t);
                desired.Add(t,path);
                if(!tracked.Any(r=>r.Target==t && ProjectFolders.Same(r.Root,path))) db.TrackSkillTarget(s.Id,t,path);
            }
            catch(WorkbenchError e){issues.Add(t.Engine+" · "+e.Message);}
        }
        foreach(var (target,root,owner) in db.SkillTargets(s.Id))
        {
            bool publish=desired.TryGetValue(target,out var current) && ProjectFolders.Same(current,root);
            try
            {
                if(!Path.IsPathFullyQualified(root)) throw new WorkbenchError("无效的发布目录",WorkbenchFailure.Forbidden);
                // 数据目录迁移保留旧库快照；引擎用户目录变更则按原始回执核对旧副本。
                bool oldData=EngineSkillRoots.PrivateTarget(target) && !ProjectFolders.Same(root,EngineSkillRoots.Destination(data,target));
                if(target.Project.Length>0 && !Directory.Exists(target.Project) && !oldData)
                    throw new WorkbenchError("项目目录暂不可访问，保留同步记录",WorkbenchFailure.Unavailable);
                else if(!oldData) SkillFiles.Apply(root,target.Name,owner,publish?package:null);
                if(!publish) db.ForgetSkillTarget(s.Id,owner);
            }
            catch(Exception e) when(e is WorkbenchError or UnauthorizedAccessException or IOException)
            {
                issues.Add(target.Engine+" · "+target.Name+" · "+e.Message);
                // 已由用户接管或存在链接的旧位置永久放弃清理授权，避免以后重试删除原生文件。
                if(!publish && e is WorkbenchError {Failure:WorkbenchFailure.Conflict})
                    db.ForgetSkillTarget(s.Id,owner);
            }
        }
        if(s.Deleted) db.FinishRetiredSkill(s.Id);
        return new(s.Deleted?null:s,issues);
    }

    private NativeSkill Native(WorkbenchStore db,string id,string? project)
    {
        var (native,_)=EngineSkillRoots.Inventory(project==null?null:db.RegisteredProject(project));
        return native.FirstOrDefault(n=>n.Id==id)??throw new WorkbenchError("原生 Skill 不在当前盘点范围",WorkbenchFailure.NotFound);
    }
    internal object ReadNative(string id,string? project)
    {
        lock(Gate)
        {
            using var db=new WorkbenchStore(data);
            var n=Native(db,id,project);
            string? issue=null;
            if(n.CanDelete)
            {
                try {
                    var (content,revision)=SkillFiles.NativePreview(n.Path);
                    return new {skill=n,content,revision,issue};
                } catch(Exception e) when(e is IOException or UnauthorizedAccessException or WorkbenchError)
                {n=n with {CanDelete=false};issue="可预览正文。当前目录不能安全核对，删除已禁用。";}
            }
            var document=ProjectFileBrowser.ReadDocument(Path.Combine(n.Path,"SKILL.md"));
            if(document.Kind!="text") throw new WorkbenchError("仅支持预览 512 KiB 内的文本");
            return new {skill=n,content=document.Content,revision=(string?)null,issue};
        }
    }
    internal void DeleteNative(string id,string? project,string? confirmName,string? revision)
    {
        lock(Gate)
        {
            using var db=new WorkbenchStore(data);
            var n=Native(db,id,project);
            if(!n.CanDelete || confirmName!=n.Name || string.IsNullOrEmpty(revision)) throw new WorkbenchError("请输入完整技能目录名并确认当前版本");
            if(db.ProjectList().Paths.Append(data).Any(p=>ProjectFolders.Same(p,n.Path) ||
                p.StartsWith(n.Path+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)))
                throw new WorkbenchError("不能删除包含工作台或登记项目的技能目录",WorkbenchFailure.Forbidden);
            var old=db.NativeSkillRemovals().Find(r=>ProjectFolders.Same(r.Path,n.Path));
            if(old!=null)
            {
                // 用户再次查看并确认的是原位置。已有隔离副本的收据仍须独立完成。
                if(old.Moved || Directory.Exists(Path.Combine(Path.GetDirectoryName(old.Path)!,".ff-retired-"+old.Id)))
                    throw new WorkbenchError("前次原生删除仍待清理",WorkbenchFailure.Conflict);
                db.FinishNativeRemoval(old.Id);
            }
            var receipt=SkillFiles.PlanNativeRemoval(n.Path,revision);
            db.AuthorizeNativeRemoval(receipt);
            ResumeNative(db,receipt);
        }
    }
    private static void ResumeNative(WorkbenchStore db,NativeSkillRemoval receipt)
    {
        try {SkillFiles.ResumeNativeRemoval(receipt,()=>db.MovedNativeSkill(receipt.Id));}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException)
        {throw new WorkbenchError("删除尚未完成。请核对待清理目录的占用、权限或只读属性，再重试已确认的删除",WorkbenchFailure.Conflict);}
        db.FinishNativeRemoval(receipt.Id);
    }
    internal void RetryNative(string id)
    {
        lock(Gate)
        {
            using var db=new WorkbenchStore(data);
            var r=db.NativeSkillRemovals().Find(r=>r.Id==id)??throw new WorkbenchError("没有待清理记录",WorkbenchFailure.NotFound);
            ResumeNative(db,r);
        }
    }
}
