import { peerPresence, remoteOnline } from "../../services/navigation";
import { eligibleLocalGroups, groupLinkSides, includedMember } from "../../services/groupLinks";
import { engineNames } from "../../services/engines";
import { approvedGroupRecipients } from "../../services/permanentGroups";
import type { Activity, Snapshot, Topic, WindowView } from "../../store/workbenchStore";

export interface Receipt { id:string; target:string; name:string; delivery:string; work?:string; acknowledged:boolean }
export interface Attachment { name:string; key:string; project?:string; path?:string }
export interface ChatPost {
  id:string; topic:string; source:string; sender:string; text:string; createdAt:string; replyTo?:string;
  attachments:Attachment[]; receipts:Receipt[]; recipients?:string[]; peerSeq?:number; kind?:string; application?:string; replyTopic?:string;
}
export interface ChatLink { id:string; name:string; leaderId?:string; leaderName?:string; peerMembers:string[]; peerMode:string }
export interface ChatPresence { activity:Activity; label?:string }
export interface ChatTarget { key:string; name:string; detail?:string; engine?:string; activity?:Activity; status?:string; kind:"agent"|"human"|"group-user" }
export const deliveryLabels:Record<string,string> = {
  pending:"排队中", writing:"投递中", sent:"已发出", accepted:"对端已入队", delivered:"已接收",
  delivery_uncertain:"待核对", ignored:"已忽略", blocked:"已拦截", cancelled:"已撤回", superseded:"已覆盖",
  no_group:"对端小组不可用",quota_exceeded:"对端额度已用完",upload_disabled:"对端附件不可用",attachment_failed:"附件投递失败"
};
export const workLabels:Record<string,string> = {working:"处理中",need_info:"待补充",completed:"已完成",replied:"已回复"};
export const position = (item:ChatPost) => item.createdAt + "/" + item.id;
export const sortPosts = (posts:ChatPost[]) => posts.sort((a,b)=>position(a).localeCompare(position(b)));
export function mergePosts(older:ChatPost[], latest:ChatPost[]) {
  const earliest = latest.at(-1);
  return sortPosts(earliest ? [...older.filter(p=>position(p)<position(earliest)),...latest] : []);
}
export function groupLinks(snapshot:Snapshot|undefined,topic:Topic): ChatLink[] {
  const links = new Map(groupLinkSides(snapshot?.groupLinks,topic.id).map(link=>[link.id,link]));
  return eligibleLocalGroups(snapshot,topic).filter(group=>links.has(group.id)).map(group=>{
    const leader=snapshot?.windows.find(view=>view.agent.id===group.leaderWindowId)?.agent;
    return {id:group.id,name:group.name,leaderId:leader?.id,leaderName:leader?.name,peerMembers:links.get(group.id)!.peerMembers,peerMode:links.get(group.id)!.peerMode};
  });
}

function localTarget({agent,activity}:WindowView, name:string, detail?:string): ChatTarget {
  return {key:agent.id,name,detail,engine:engineNames[agent.engine]??agent.engine,activity,kind:"agent"};
}

const available = (target:ChatTarget) => target.activity!=null && target.activity!=="offline" && target.activity!=="finished";
const onlineFirst = (a:ChatTarget,b:ChatTarget) => Number(available(b))-Number(available(a));

export function chatTargets(snapshot:Snapshot|undefined,topic:Topic,links:ChatLink[]): ChatTarget[] {
  if (!snapshot) return [];
  const members = snapshot.windows.filter(w=>w.agent.topicId===topic.id).map(w=>localTarget(w,"@"+w.agent.name)).sort(onlineFirst);
  const external:ChatTarget[]=[];
  for (const recipient of approvedGroupRecipients(snapshot.federation?.groups,topic.id))
    external.push({key:recipient.key,name:"@"+recipient.name,detail:recipient.topic+" · "+recipient.user,
      engine:recipient.engine?engineNames[recipient.engine]??recipient.engine:undefined,
      activity:recipient.engine?(recipient.online?"idle":"offline"):undefined,
      status:recipient.engine?peerPresence(recipient.online,recipient.connected):undefined,kind:recipient.engine?"agent":"group-user"});
  for (const link of topic.leaderWindowId?links:[]) {
    if (!link.leaderId) continue;
    for (const view of snapshot.windows.filter(({agent})=>agent.topicId===link.id&&
      includedMember(link.peerMode,link.peerMembers,agent.id,agent.id===link.leaderId)))
      external.push(localTarget(view,"@"+link.name+"/"+view.agent.name,view.agent.id===link.leaderId?"合作组长":"合作成员"));
  }
  for (const remote of snapshot.federation?.remotes ?? []) {
    if (remote.topicId!==topic.id || remote.status!=="approved") continue;
    const online = remoteOnline(remote);
    external.push({key:remote.id,name:"@"+remote.agentName,detail:remote.nodeName+" · 外部 Agent",kind:"agent",
      activity:online?"idle":"offline",status:peerPresence(online,remote.connected)});
    if (topic.leaderWindowId&&!remote.deniedAgentIds?.includes(topic.leaderWindowId))
      external.push({key:"remote:"+remote.id,name:"@"+remote.nodeName+" · 用户",detail:"跨端用户",kind:"human"});
  }
  for (const grant of snapshot.federation?.grants ?? []) {
    if (!topic.leaderWindowId || grant.windowId!==topic.leaderWindowId || grant.status!=="approved" || grant.paused) continue;
    const contacts=snapshot.federation?.contacts?.filter(c=>c.grantId===grant.id&&c.canMention)??[];
    if (!contacts.length) {
      external.push({key:"grant:"+grant.id,name:"@"+grant.nodeName+" · 用户",detail:"跨端用户",kind:"human"});
    } else {
      for (const contact of contacts)
        external.push({key:"grant:"+grant.id+":"+contact.id,name:"@"+grant.nodeName+" · 用户",detail:"跨端用户 · "+contact.name,kind:"human"});
    }
  }
  return [...members,...external.sort(onlineFirst)];
}
