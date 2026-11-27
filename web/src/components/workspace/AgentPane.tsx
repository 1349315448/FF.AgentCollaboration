import { useLayoutEffect } from "react";
import { PaneContext, type PanePresentation } from "./panePresentation";
import { PaneHeader } from "./PaneHeader";
import { InboxBanner } from "./InboxBanner";
import { TerminalMount } from "./TerminalMount";
import { PaneToolbar } from "./PaneToolbar";

interface Props { element: HTMLElement; slot: HTMLElement; presentation?: PanePresentation }
export function AgentPane({element, slot, presentation}: Props) {
  useLayoutEffect(() => {
    if (element.parentElement !== slot) slot.append(element);
  }, [element,slot]);
  return (
    <PaneContext.Provider value={presentation}>
      <PaneHeader />
      <InboxBanner />
      <TerminalMount />
      <PaneToolbar />
      <details className="saved-editor-draft" hidden>
        <summary>查看暂存草稿</summary>
        <textarea aria-label="暂存的原文，可复制回终端" rows={2} readOnly />
        <button type="button" className="text-button">清除副本</button>
      </details>
      <p className="pane-error form-error" role="alert" hidden></p>
    </PaneContext.Provider>
  );
}
