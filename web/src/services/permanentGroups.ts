export interface GroupRecipient {
  key:string; window:string; name:string; topic:string; user:string; engine?:string; leader:boolean; online:boolean;
}
export interface GroupPolicy {
  permissionMode:string; quotaMode:string; quotaLimit?:number|null; paused:boolean; allowUpload:boolean; used?:number; status?:string;
}
export interface GroupCollaborationView {
  id:string; topic:string; status:string; requester:boolean; connected:boolean; user:string; purpose:string;
  memberMode?:string; suggestedMember?:string; expiresAt?:number|null; members:string[]; policy:GroupPolicy; remoteAllowsUpload:boolean; remoteQuota?:{remaining?:number|null};
  remote:{topic:string;name:string;leader:string;active:boolean;members:{id:string;name:string;online:boolean;engine:string}[]};
  recipients:GroupRecipient[];
}

export const groupStates:Record<string,string> = {
  applying:"申请中", pending:"待审批", approved:"协作中", paused:"已暂停", rejected:"已拒绝", revoked:"已解除"
};

export function openGroupCollaboration(id?:string,tab:"local"|"external"="external",member?:string) {
  window.dispatchEvent(new CustomEvent("ff:group-collaboration",{detail:{id,tab,member}}));
}

export function approvedGroupRecipients(groups:GroupCollaborationView[]|undefined,topic:string,actor?:string) {
  return (groups??[]).filter(group=>group.topic===topic && group.status==="approved" &&
    (!actor||group.members.includes(actor))).flatMap(group=>group.recipients);
}
