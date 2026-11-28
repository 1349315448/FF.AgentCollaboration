using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace FF.AgentCollaboration.Updates;

internal sealed record UpdatePlan(string Id,string? Candidate,string Version);
internal sealed record UpdateResult(string Id,string Status,string Version);

// 启动器留在原安装目录，版本目录和应用进程可以独立切换。
internal static class UpdateSupervisor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal const string RootVariable="FF_UPDATE_ROOT";
    private const string BootVariable="FF_UPDATE_BOOT";
    internal static string? Root => Environment.GetEnvironmentVariable(RootVariable);
    internal static string? Boot => Environment.GetEnvironmentVariable(BootVariable);
    internal static string? InstallHome => Root==null ? null : Directory.GetParent(Root)!.Parent!.FullName;
    internal static string Family(string product) => product=="workbench" ? "FF.AgentCollaboration" : "FF.AgentCollaboration.Signaling";
    internal static string Executable(string product) => Family(product)+(OperatingSystem.IsWindows()?".exe":"");
    internal static string Version(Assembly assembly) => assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion.Split('+')[0] ?? assembly.GetName().Version!.ToString(3);

    internal static async Task<int> Run(string product,string[] args)
    {
        string home=Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        string root=Path.Combine(home,".ff-updates",product);
        Directory.CreateDirectory(root);
        if(!OperatingSystem.IsWindows()) File.SetUnixFileMode(root,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        using var single=new FileStream(Path.Combine(root,"launcher.lock"),FileMode.OpenOrCreate,FileAccess.Write,FileShare.None);
        var active=Read<UpdatePlan>(root,"active.json");
        string current=active?.Candidate==null?home:CandidatePath(root,active.Candidate);
        using var stop=new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel=(_,e)=>{ e.Cancel=true;stop.Cancel(); };
        Console.CancelKeyPress+=onCancel;
        Process? process=null;
        try
        {
            // 中断的切换不授予启动确认。下次启动继续使用上一次通过健康检查的安装。
            if(Read<UpdatePlan>(root,"pending.json") is { } interrupted) Complete(root,interrupted,"failed");
            process=Start(product,args,root,current,null);
            while(!stop.IsCancellationRequested)
            {
                await process.WaitForExitAsync(stop.Token);
                int exit=process.ExitCode;
                process.Dispose();
                process=null;
                if(Read<UpdatePlan>(root,"pending.json") is not { } plan) return exit;
                string previous=current;
                try
                {
                    current=plan.Candidate==null?current:CandidatePath(root,plan.Candidate);
                    process=Start(product,args,root,current,plan.Id,showBrowser:false);
                    if(!await Healthy(root,plan.Id,process,stop.Token)) throw new IOException("新版没有通过启动检查");
                    if(plan.Candidate!=null) Write(root,"active.json",plan);
                    Complete(root,plan,"applied");
                }
                catch(Exception error) when(!stop.IsCancellationRequested &&
                    error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                {
                    await End(process);
                    process?.Dispose();
                    process=null;
                    Complete(root,plan,"failed");
                    Console.Error.WriteLine("自动更新启动失败，正在恢复上一次安装。");
                    current=previous;
                    process=Start(product,args,root,current,null,showBrowser:false);
                }
            }
            return 0;
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested) { return 0; }
        finally
        {
            Console.CancelKeyPress-=onCancel;
            await End(process);
            process?.Dispose();
        }
    }

    private static Process Start(string product,string[] args,string root,string directory,string? boot,bool showBrowser=true)
    {
        var info=new ProcessStartInfo(Path.Combine(directory,Executable(product)))
        { UseShellExecute=false,WorkingDirectory=Directory.GetCurrentDirectory() };
        foreach(string arg in args)
            if(showBrowser || product!="workbench" || arg!="--open-browser") info.ArgumentList.Add(arg);
        info.Environment[RootVariable]=root;
        if(boot!=null)info.Environment[BootVariable]=boot;
        else info.Environment.Remove(BootVariable);
        if(product=="workbench" && !info.Environment.ContainsKey("FF_WORKBENCH_HOME"))
            info.Environment["FF_WORKBENCH_HOME"]=Directory.GetParent(root)!.Parent!.FullName;
        return Process.Start(info) ?? throw new IOException("无法启动工作台或信令服务");
    }

    private static async Task End(Process? process)
    {
        if(process==null || process.HasExited) return;
        process.Kill(entireProcessTree:true);
        await process.WaitForExitAsync();
    }

    private static async Task<bool> Healthy(string root,string id,Process process,CancellationToken ct)
    {
        for(int probe=0;probe<160 && !process.HasExited;probe++)
        {
            if(File.Exists(Path.Combine(root,id+".ready")))return true;
            await Task.Delay(250,ct);
        }
        return false;
    }

    internal static void Ready(string version)
    {
        if(Root is string root && Boot is string id &&
            Read<UpdatePlan>(root,"pending.json") is { } plan && plan.Id==id && plan.Version==version)
            Write(root,id+".ready",id);
    }

    internal static async Task<string?> ConfirmedBoot(CancellationToken ct)
    {
        if(Root is not { } root || Boot is not { } id)return null;
        while(!ct.IsCancellationRequested)
        {
            var result=Read<UpdateResult>(root,"result.json");
            if(result?.Id==id) return result.Status=="applied"?id:null;
            await Task.Delay(250,ct);
        }
        return null;
    }

    private static void Complete(string root,UpdatePlan plan,string status)
    {
        Write(root,"result.json",new UpdateResult(plan.Id,status,plan.Version));
        File.Delete(Path.Combine(root,"pending.json"));
        File.Delete(Path.Combine(root,plan.Id+".ready"));
    }

    internal static string CandidatePath(string root,string name) => Guid.TryParseExact(name,"N",out _)
        ? Path.Combine(root,"versions",name) : throw new InvalidDataException("更新目录编号无效");

    internal static T? Read<T>(string root,string part)
    {
        string path=Path.Combine(root,part);
        return File.Exists(path)?JsonSerializer.Deserialize<T>(File.ReadAllBytes(path),Json):default;
    }

    internal static void Write<T>(string root,string part,T value)
    {
        string path=Path.Combine(root,part);
        string temp=path+"."+Guid.NewGuid().ToString("N");
        try
        {
            using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write))
            {
                JsonSerializer.Serialize(file,value,Json);
                file.Flush(flushToDisk:true);
            }
            File.Move(temp,path,overwrite:true);
        }
        finally { File.Delete(temp); }
    }
}
