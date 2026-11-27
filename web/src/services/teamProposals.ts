export interface TeamSuggestion {
  name: string;
  role: string;
  taskSummary: string;
  remoteId?: string | null;
  suggestedEngine?: string | null;
  suggestedModel?: string | null;
  suggestedEffort?: string | null;
}

export interface TeamProposal {
  id: string;
  topicId: string;
  leaderWindowId: string;
  leaderName: string;
  summary: string;
  members: TeamSuggestion[];
  status: string;
}

export interface CreatedTeam {
  status: "created" | "partial" | "failed";
  members: {index:number; name:string; windowId?:string; remoteId?:string; online:boolean; error?:string}[];
  rosterWarning?: string;
}
