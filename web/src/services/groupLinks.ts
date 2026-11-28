import type {GroupPair,Snapshot,Topic} from "../store/workbenchStore";
import {projectKey} from "./navigation";

export function groupLinkSides(pairs:GroupPair[]|undefined,topic:string) {
  return (pairs??[]).flatMap(pair=>pair.first===topic
    ? [{id:pair.second,members:pair.firstMembers??[],peerMembers:pair.secondMembers??[],mode:pair.firstMode??"legacy",peerMode:pair.secondMode??"legacy"}]
    : pair.second===topic ? [{id:pair.first,members:pair.secondMembers??[],peerMembers:pair.firstMembers??[],mode:pair.secondMode??"legacy",peerMode:pair.firstMode??"legacy"}] : []);
}

export function includedMember(mode:string,ids:string[],id:string,leader:boolean) {
  return mode==="all" || ids.includes(id) || mode==="legacy" && leader;
}

export function eligibleLocalGroups(snapshot:Snapshot|undefined,topic:Topic) {
  return (snapshot?.topics??[]).filter(group=>group.id!==topic.id&&group.status==="active"&&
    projectKey(group.project)===projectKey(topic.project)).sort((a,b)=>a.name.localeCompare(b.name));
}
