import { createRoot, type Root } from "react-dom/client";
import { flushSync } from "react-dom";
import { createElement } from "react";
import type { PanePresentation } from "../components/workspace/panePresentation";
import { AgentPane } from "../components/workspace/AgentPane";

interface TerminalHandle { dispose(): void; open(element: HTMLElement): void; loadAddon(addon: FitHandle): void }
interface FitHandle { fit(): void }
interface PaneHost {
  root: Root; element: HTMLElement; slot: HTMLElement; presentation?: PanePresentation;
}
interface PaneRuntime extends PaneHost {
  term: TerminalHandle; observer: ResizeObserver;
  socket?: WebSocket; runningTimer?: number; reconnectTimer?: number;
}
declare const Terminal: new(options: object) => TerminalHandle;
declare const FitAddon: { FitAddon: new() => FitHandle };

// One runtime per window ID, including windows outside the currently selected topic.
export const panes = new Map<string, PaneRuntime>();
export function mountTerminal(container: HTMLElement) {
  const element = document.createElement("article");
  element.className = "pane";
  const root = createRoot(element);
  const host = { root, element, slot: container };
  renderPane(host);
  const term = new Terminal({
    fontFamily: '"Cascadia Mono", Consolas, "Microsoft YaHei UI", monospace',
    fontSize: 13, cursorBlink: true, scrollback: 4000,
    theme: { background: "#0a0a0d", foreground: "#e4e4e7", cursor: "#f4f4f5", selectionBackground: "#27272a" },
    windowsPty: { backend: "conpty" }
  });
  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  const viewport = element.querySelector<HTMLElement>(".terminal-viewport")!;
  term.open(viewport);
  return { ...host, term, fit, viewport };
}

function renderPane(host: PaneHost) {
  const { element, slot, presentation } = host;
  flushSync(() => host.root.render(createElement(AgentPane, {element, slot, presentation})));
}
export function movePane(host: PaneHost, slot: HTMLElement) {
  if (host.slot === slot) return;
  host.slot = slot;
  renderPane(host);
}
export function presentPane(host: PaneHost, next: PanePresentation) {
  const previous = host.presentation;
  if (previous && (Object.keys(next) as (keyof PanePresentation)[]).every(key => previous[key] === next[key])) return;
  host.presentation = next;
  renderPane(host);
}
export function removeMissingWindows(ids: Set<string>) {
  for (const [id, pane] of panes) {
    if (ids.has(id)) continue;
    panes.delete(id);
    clearTimeout(pane.runningTimer);
    clearTimeout(pane.reconnectTimer);
    pane.observer.disconnect();
    const socket = pane.socket;
    pane.socket = undefined;
    socket?.close();
    pane.term.dispose();
    pane.root.unmount();
    pane.element.remove();
  }
}
