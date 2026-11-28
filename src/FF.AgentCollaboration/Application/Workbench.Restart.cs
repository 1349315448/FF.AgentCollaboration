using FF.AgentCollaboration.Infrastructure.Storage;

namespace FF.AgentCollaboration.Application;

internal sealed partial class Workbench
{
    private async Task RestoreStartupAgents(CancellationToken ct)
    {
        string? operation = await FF.AgentCollaboration.Updates.UpdateSupervisor.ConfirmedBoot(ct);
        using var state = new WorkbenchStore(Root);
        foreach (string window in state.StartupWindows(operation))
        {
            await Start(window, ct, startupRecovery:true);
            if (operation != null) state.ReleaseRestartTasks(operation,window);
            events.Changed();
        }
    }

    internal bool IdleForUpdate() => terminals.Values.All(terminal =>
    {
        var (online,ready,activity) = terminal.ViewState();
        return !online || ready && activity=="idle";
    });
}
