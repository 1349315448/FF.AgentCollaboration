using System.Text.Json;
using FF.AgentCollaboration.Domain;

namespace FF.AgentCollaboration.Infrastructure.Storage;

internal sealed partial class WorkbenchStore
{
    private void InitializeManagedSkills() => Exec("""
        CREATE TABLE IF NOT EXISTS managed_skills(
          id TEXT PRIMARY KEY,name TEXT NOT NULL COLLATE NOCASE UNIQUE,description TEXT NOT NULL,
          scope TEXT NOT NULL,project_path TEXT,engines TEXT NOT NULL,enabled INTEGER NOT NULL,
          revision TEXT NOT NULL,deleted INTEGER NOT NULL,package BLOB NOT NULL);
        CREATE TABLE IF NOT EXISTS skill_targets(
          skill TEXT NOT NULL,engine TEXT NOT NULL,project TEXT NOT NULL,name TEXT NOT NULL,root TEXT NOT NULL,owner TEXT NOT NULL,
          PRIMARY KEY(skill,engine,project,name,root));
        CREATE TABLE IF NOT EXISTS native_skill_removals(
          id TEXT PRIMARY KEY,path TEXT NOT NULL UNIQUE,revision TEXT NOT NULL,files TEXT NOT NULL,dirs TEXT NOT NULL,moved INTEGER NOT NULL);
        """);

    internal List<ManagedSkill> Skills(bool includeDeleted=false) => Read("""
        SELECT id,name,description,scope,project_path,engines,enabled,revision,deleted FROM managed_skills
        WHERE deleted=0 OR $p0=1 ORDER BY name
        """,r=>new ManagedSkill(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),Optional(r,4),
            JsonSerializer.Deserialize<string[]>(r.GetString(5))!,r.GetBoolean(6),r.GetString(7),r.GetBoolean(8)),includeDeleted);

    internal ManagedSkill Skill(string id) => Skills().Find(s=>s.Id==id) ?? throw new WorkbenchError("Skill 已删除",WorkbenchFailure.NotFound);

    // SQLite 在线备份一并包含源包。迁移数据目录后可以安全重建磁盘库和发布副本。
    internal Dictionary<string,byte[]> SkillPackage(string id) => Read("SELECT package FROM managed_skills WHERE id=$p0",
        r=>JsonSerializer.Deserialize<Dictionary<string,byte[]>>((byte[])r[0])!,id).Single();
    internal List<(SkillTarget Target,string Root,string Owner)> SkillTargets(string id) => Read("SELECT engine,project,name,root,owner FROM skill_targets WHERE skill=$p0",
        r=>(new SkillTarget(r.GetString(0),r.GetString(1),r.GetString(2)),r.GetString(3),r.GetString(4)),id);
    internal void TrackSkillTarget(string id,SkillTarget t,string path) =>
        Exec("INSERT OR IGNORE INTO skill_targets VALUES($p0,$p1,$p2,$p3,$p4,$p5)",id,t.Engine,t.Project,t.Name,path,Guid.NewGuid().ToString("N"));

    internal void SaveSkill(ManagedSkill skill,Dictionary<string,byte[]> files,string? previousRevision)
    {
        Atomic(()=>{
            var current=Skills(true);
            var old=current.Find(s=>s.Id==skill.Id);
            if(old?.Revision!=previousRevision || old?.Deleted==true) throw new WorkbenchError("Skill 已变更，请重新打开",WorkbenchFailure.Conflict);
            if(current.Any(s=>s.Id!=skill.Id && s.Name==skill.Name)) throw new WorkbenchError("技能名已存在",WorkbenchFailure.Conflict);
            Exec("""
                INSERT INTO managed_skills VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,0,$p8)
                ON CONFLICT(id) DO UPDATE SET name=excluded.name,description=excluded.description,scope=excluded.scope,
                project_path=excluded.project_path,engines=excluded.engines,enabled=excluded.enabled,revision=excluded.revision,package=excluded.package
                """,skill.Id,skill.Name,skill.Description,skill.Scope,skill.ProjectPath,JsonSerializer.Serialize(skill.Engines),
                skill.Enabled,skill.Revision,JsonSerializer.SerializeToUtf8Bytes(files));
        });
    }
    internal void RetireSkill(string id,string revision)
    {
        if(Exec("UPDATE managed_skills SET deleted=1 WHERE id=$p0 AND revision=$p1 AND deleted=0",id,revision)!=1)
            throw new WorkbenchError("Skill 已变更，请重新打开",WorkbenchFailure.Conflict);
    }
    internal void ForgetSkillTarget(string id,string owner) =>
        Exec("DELETE FROM skill_targets WHERE skill=$p0 AND owner=$p1",id,owner);
    internal void FinishRetiredSkill(string id) => Exec("DELETE FROM managed_skills WHERE id=$p0 AND deleted=1 AND NOT EXISTS(SELECT 1 FROM skill_targets WHERE skill=$p0)",id);

    internal List<NativeSkillRemoval> NativeSkillRemovals() => Read("SELECT id,path,revision,files,dirs,moved FROM native_skill_removals",
        r=>new NativeSkillRemoval(r.GetString(0),r.GetString(1),r.GetString(2),JsonSerializer.Deserialize<Dictionary<string,string>>(r.GetString(3))!,
            JsonSerializer.Deserialize<string[]>(r.GetString(4))!,r.GetBoolean(5)));
    internal void AuthorizeNativeRemoval(NativeSkillRemoval r) => Exec("INSERT INTO native_skill_removals VALUES($p0,$p1,$p2,$p3,$p4,0)",
        r.Id,r.Path,r.Revision,JsonSerializer.Serialize(r.Files),JsonSerializer.Serialize(r.Directories));
    internal void MovedNativeSkill(string id)=>Exec("UPDATE native_skill_removals SET moved=1 WHERE id=$p0",id);
    internal void FinishNativeRemoval(string id)=>Exec("DELETE FROM native_skill_removals WHERE id=$p0",id);
}
