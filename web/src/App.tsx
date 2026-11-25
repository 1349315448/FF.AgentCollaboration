import { TopBar } from "./components/layout/TopBar";
import { ProjectRail } from "./components/layout/ProjectRail";
import { TopicHeader } from "./components/workspace/TopicHeader";
import { WorkspaceLayout } from "./components/workspace/WorkspaceLayout";
import { EmptyWorkspace } from "./components/workspace/EmptyWorkspace";
import { CollabLogSection } from "./components/workspace/CollabLogSection";
import { RenameDialog } from "./components/dialogs/RenameDialog";
import { TopicDialog } from "./components/dialogs/TopicDialog";
import { NewAgentDialog } from "./components/dialogs/NewAgentDialog";
import { PaneSessionDialog } from "./components/dialogs/PaneSessionDialog";
import { SetupDialog } from "./components/dialogs/SetupDialog";
import { SettingsDrawer } from "./components/dialogs/SettingsDrawer";
import { FolderDialog } from "./components/dialogs/FolderDialog";
import { MentionPickerPopover } from "./components/dialogs/MentionPickerPopover";
import { ShortcutsDialog } from "./components/dialogs/ShortcutsDialog";
import { FilesDialog } from "./components/dialogs/FilesDialog";
import { PreviewDialog } from "./components/dialogs/PreviewDialog";

export function App() {
  return (
    <>
      <TopBar />
      <main className="workspace" aria-label="终端工作区">
        <p id="mention-notice" className="banner" role="status" hidden></p>
        <p id="connection-error" className="banner" role="alert" hidden></p>
        <div id="topic-shell" className="topic-shell">
          <ProjectRail />
          <section className="topic-workspace">
            <TopicHeader />
            <WorkspaceLayout />
            <EmptyWorkspace />
            <CollabLogSection />
          </section>
        </div>
      </main>
      <RenameDialog />
      <TopicDialog />
      <NewAgentDialog />
      <PaneSessionDialog />
      <SetupDialog />
      <SettingsDrawer />
      <FolderDialog />
      <MentionPickerPopover />
      <ShortcutsDialog />
      <FilesDialog />
      <PreviewDialog />
    </>
  );
}
