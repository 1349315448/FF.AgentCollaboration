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
      <p className="pane-error form-error" role="alert" hidden></p>
    </PaneContext.Provider>
  );
}
