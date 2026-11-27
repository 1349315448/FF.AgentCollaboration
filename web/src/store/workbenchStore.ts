import { useSyncExternalStore } from "react";
import { projectKey } from "../services/navigation";
import type { Federation } from "../components/federation/Federation";
import type { KnowledgeMode } from "../services/knowledge";
import type { TeamProposal } from "../services/teamProposals";

export type Activity = "offline" | "idle" | "working" | "waiting" | "finished";
export interface Topic { id: string; name: string; project: string; status: string; activity: Activity; goal?: string; finishSummary?: string; leaderWindowId?: string|null }
export interface Agent { role?: string|null; id: string; name: string; topicId: string; engine: string; online: boolean; project: string; autoReceive: boolean; receiveMode?: "queued" | "immediate"; isLeader?: boolean }
export interface Message { id: string; state: string; from: string; fromTopic?: string; acknowledged?: boolean }
export interface WindowView { agent: Agent; activity: Activity; inbox: Message[]; terminal?: string; exitCode?: number; wakeError?: string }
export interface TimelineEntry { id: string; source?:string; state: string; from: string; to: string; summary: string; acknowledged?: boolean; createdAt?: string; hasDocument?:boolean; workState?:string|null }
export interface HandoffRoute { id: string; source: string; target: string; state: string; acknowledged: boolean }
export interface Snapshot { project?: string; projects: string[]; topics: Topic[]; windows: WindowView[]; timelineTopic?: string; timeline: TimelineEntry[]; recentHandoffs?: HandoffRoute[]; federation?: Federation; knowledgeModes?: Record<string,KnowledgeMode>; teamProposals?: TeamProposal[] }
export interface Layout {
  primaryWindowId: string | null; activeWindowId: string | null; collapsedIds: string[]; splitRatio: number;
  workspaceMode: "agent" | "code"; agentInspectorOpen: boolean; agentInspectorRatio: number;
  codeAgentRatio: number; codeAgentHidden: boolean; codeActiveWindowId: string | null;
}
export interface ProjectTotals { topics: number; agents: number; unread: number; activity: Activity; working: number; idle: number; offline: number }

export let snapshot: Snapshot | undefined;
export let selectedTopicId: string | undefined;
export let layout: Layout;
export let selectedProject: string | undefined;
export let mentionShortcut = "Alt+Shift+A";
export function setSnapshot(value: Snapshot) { snapshot = value; }
export function setSelectedProjectPath(value: string) { selectedProject = value; }
export function setMentionShortcut(value: string) { mentionShortcut = value; }
export function setTopic(id: string | undefined, next: Layout) { selectedTopicId = id; layout = next; }

// Native dialogs also change state on Escape and light dismiss. Read their live state.
export function isDialogOpen(id?: string) {
  if (!id) return !!document.querySelector("dialog[open]");
  const dialog = document.getElementById(id);
  return dialog instanceof HTMLDialogElement && dialog.open;
}

function buildFrame() {
  const totals = new Map<string,ProjectTotals>();
  const rosters = new Map<string,{ project: string; members: WindowView[]; unread: number }>();
  for (const path of snapshot?.projects ?? []) {
    totals.set(projectKey(path), { topics:0, agents:0, unread:0, working:0, idle:0, offline:0, activity:"idle" });
  }
  for (const topic of snapshot?.topics ?? []) {
    const key = projectKey(topic.project), total = totals.get(key);
    rosters.set(topic.id,{ project:key, members:[], unread:0 });
    if (total) {
      total.topics++;
      if (topic.activity === "working" || (topic.activity === "waiting" && total.activity !== "working")) total.activity = topic.activity;
    }
  }
  for (const view of snapshot?.windows ?? []) {
    const roster = rosters.get(view.agent.topicId);
    if (!roster) continue;
    const unread = view.inbox.filter(item => item.state === "pending" || item.state === "delivery_uncertain").length;
    roster.members.push(view);
    roster.unread += unread;
    const total = totals.get(roster.project);
    if (!total) continue;
    total.agents++;
    total.unread += unread;
    if (unread && total.activity === "idle") total.activity = "waiting";
    if (view.activity === "idle") total.idle++;
    else if (view.activity === "offline") total.offline++;
    else total.working++;
  }
  return { snapshot, selectedTopicId, topic:snapshot?.topics.find(t => t.id === selectedTopicId), totals, rosters };
}
let frame = buildFrame();
const listeners = new Set<() => void>();
function subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; }
function read() { return frame; }
export function publishFrame() {
  frame = buildFrame();
  listeners.forEach(listener => listener());
}
export function useWorkbench() { return useSyncExternalStore(subscribe, read); }
