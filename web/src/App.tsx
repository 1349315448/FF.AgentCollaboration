import { useState } from "react";
import { TopBar } from "./components/layout/TopBar";
import { ProjectRail } from "./components/layout/ProjectRail";

import { WorkspaceLayout } from "./components/workspace/WorkspaceLayout";
import { InspectorShell } from "./components/inspector/InspectorShell";
import { GitWorkspace } from "./components/inspector/gitWorkspace";
import { EmptyWorkspace } from "./components/workspace/EmptyWorkspace";
import { CollabLogSection } from "./components/workspace/CollabLogSection";
import { AgentConfigDialog } from "./components/dialogs/AgentConfigDialog";
import { TopicDialog } from "./components/dialogs/TopicDialog";
import { NewAgentDialog } from "./components/dialogs/NewAgentDialog";

import { SetupDialog } from "./components/dialogs/SetupDialog";
import { SettingsDrawer } from "./components/dialogs/SettingsDrawer";
import { FolderDialog } from "./components/dialogs/FolderDialog";
import { MentionPickerPopover } from "./components/dialogs/MentionPickerPopover";
import { ShortcutsDialog } from "./components/dialogs/ShortcutsDialog";
import { FilesDialog } from "./components/dialogs/FilesDialog";
import { PreviewDialog } from "./components/dialogs/PreviewDialog";
import { FederationDialog } from "./components/federation/Federation";

export function App() {
  const [toolbar, setToolbar] = useState<HTMLDivElement | null>(null);
  return (
    <GitWorkspace>
      <div id="topic-shell" className="topic-shell">
        <ProjectRail />
        <TopBar toolbarRef={setToolbar} />
        <main className="workspace" aria-label="终端工作区">
          <p id="mention-notice" className="banner" role="status" hidden></p>
          <p id="connection-error" className="banner" role="alert" hidden></p>
          <div id="navigation-loading" role="status" aria-live="polite" hidden><span aria-hidden="true"/><span id="navigation-target"/></div>
          <div id="workspace-stage">
            <section className="topic-workspace" id="agent-stage">
              <div id="terminal-parking" hidden />
              <WorkspaceLayout />
              <EmptyWorkspace />
              <CollabLogSection />
            </section>
            <div id="inspector-divider" className="pane-divider" role="separator" tabIndex={0} aria-orientation="vertical"
              aria-label="调整 Agent 与代码面板宽度" aria-controls="agent-stage inspector-shell" aria-valuemin={22} aria-valuemax={45} aria-valuenow={30} hidden/>
            <InspectorShell toolbarHost={toolbar} />
          </div>
        </main>
      </div>
      <AgentConfigDialog />
      <TopicDialog />
      <NewAgentDialog />

      <SetupDialog />
      <SettingsDrawer />
      <FolderDialog />
      <MentionPickerPopover />
      <ShortcutsDialog />
      <FilesDialog />
      <PreviewDialog />
      <FederationDialog />
    </GitWorkspace>
  );
}
