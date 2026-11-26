import { createContext, useContext } from "react";

export interface PanePresentation {
  id: string; name: string; engine: string; project: string; online: boolean; status: string; exitText: string;
  powerAction?: "start" | "stop";
  inboxState?: string; caption: string; inboxCount: number; ignoreCount: number; canIgnore: boolean;
}
export const PaneContext = createContext<PanePresentation | undefined>(undefined);
export const usePane = () => useContext(PaneContext);
