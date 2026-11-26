import type { Activity } from "../store/workbenchStore";

export const activityLabels: Record<Activity,string> = { offline:"离线", idle:"待命", working:"进行中", waiting:"需要处理", finished:"已结束" };
export function projectKey(path: string) { return path.replaceAll("/", "\\").replace(/\\+$/, "").toLowerCase(); }

export interface NavigationActions {
  removeProject(path:string):void;
  addTopic(path:string):void;
  switchTopic(id:string):Promise<void>;
  editTopic(id:string):void;
  archiveTopic(id:string):void;
  selectAgent(topicId:string,id:string):void;
  renameAgent(id:string):void;
  powerAgent(id:string):void;
  addAgent():void;
}
// Installed once after the host DOM exists and before publishing the first snapshot.
export const navigation = {} as NavigationActions;
