import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { request } from "../../services/api";
import { navigation, remoteOnline } from "../../services/navigation";
import { useWorkbench, type Topic } from "../../store/workbenchStore";
import { approvedGroupRecipients,openGroupCollaboration } from "../../services/permanentGroups";
import { NavigationName } from "../layout/NavigationName";
import { ChatComposer, emptyDraft, type ChatDraft } from "./ChatComposer";
import { ChatThread } from "./ChatThread";
import { chatTargets, groupLinks, mergePosts, position, type ChatPost, type ChatPresence } from "./chatModel";
import "../../styles/chat.css";

export function GroupChat() {
  const {topic} = useWorkbench(), drafts=useRef(new Map<string,ChatDraft>());
  return topic ? <Group key={topic.id} topic={topic} drafts={drafts.current}/> : null;
}

function Group({topic,drafts}:{topic:Topic;drafts:Map<string,ChatDraft>}) {
  const {snapshot,rosters} = useWorkbench();
  const [draft,setDraft]=useState(()=>drafts.get(topic.id)??emptyDraft());
  const [posts,setPosts]=useState<ChatPost[]>([]), [history,setHistory]=useState<ChatPost[]>([]);
  const [more,setMore]=useState(true), [loading,setLoading]=useState(false);
  const [notice,setNotice]=useState(""), [unread,setUnread]=useState(0);
  const list = useRef<HTMLDivElement>(null), atBottom=useRef(true);
  const known=useRef<Set<string>|null>(null), restoreTop=useRef<{height:number;top:number}|null>(null);
  const latest=useRef<string>(""),synchronized=useRef(0),requests=useRef(0);
  const displayedRef=useRef<ChatPost[]>([]);
  const route="/topics/"+topic.id+"/chat";
  const sync=useCallback(async(signal?:AbortSignal)=>{
    const requestIndex=++requests.current;
    const data = await request<ChatPost[]>(route,{signal});
    const earliest=data.at(-1);
    const older=data.length===40 && earliest ? displayedRef.current.filter(post=>position(post)<position(earliest)):[];
    const refresh=older.filter(post=>post.receipts.some(r=>!r.acknowledged &&
      (["pending","writing","sending","accepted","delivery_uncertain"].includes(r.delivery)||
       ["queued","received","working","need_info"].includes(r.work??"")))).map(post=>post.id);
    const batches:Promise<ChatPost[]>[]=[];
    for(let offset=0;offset<refresh.length;offset+=40)
      batches.push(request<ChatPost[]>(route+"?refresh="+encodeURIComponent(refresh.slice(offset,offset+40).join(",")),{signal}));
    const changed=new Map((await Promise.all(batches)).flat().map(post=>[post.id,post]));
    if(signal?.aborted || requestIndex<synchronized.current) return;
    synchronized.current=requestIndex;
    if(known.current){
      const newPosts=data.filter(post=>position(post)>latest.current && !known.current!.has(post.id));
      if(newPosts.length && !atBottom.current) setUnread(count=>count+newPosts.length);
      for(const post of newPosts.filter(p=>p.receipts.length||p.kind==="human-in"||p.replyTo||p.source!=="workbench-user").slice(0,4))
        window.dispatchEvent(new CustomEvent("ff:chat-flight",{detail:post}));
    }
    known.current=new Set(data.map(item=>item.id));
    latest.current=data[0]?position(data[0]):"";
    const refreshed=new Set(refresh);
    setHistory(current=>data.length<40 ? [] : [...new Map([...current,...older].map(post=>[post.id,post])).values()]
      .filter(post=>!refreshed.has(post.id)||changed.has(post.id)).map(post=>changed.get(post.id)??post));
    setPosts(data);if(data.length<40)setMore(false);
  },[route]);
  useEffect(()=>{
    const control = new AbortController();
    let task:Promise<void>|undefined, pending=false, scheduled:number|undefined;
    const read=()=>{
      if(control.signal.aborted)return;
      if(task){pending=true;return;}
      task=sync(control.signal).catch(error=>{if(!control.signal.aborted)setNotice(error.message);}).finally(()=>{
        task=undefined;if(pending){pending=false;schedule();}
      });
    };
    const schedule=()=>{if(scheduled==null)scheduled=window.setTimeout(()=>{scheduled=undefined;read();},160);};
    read();window.addEventListener("ff:state-changed",schedule);
    return ()=>{
      control.abort();window.clearTimeout(scheduled);
      window.removeEventListener("ff:state-changed",schedule);
    };
  },[sync]);
  function bottom(){if(list.current)list.current.scrollTop=list.current.scrollHeight;atBottom.current=true;setUnread(0);}
  const displayed=useMemo(()=>mergePosts(history,posts),[history,posts]);
  displayedRef.current=displayed;
  useLayoutEffect(()=>{
    if(!list.current)return;
    if(restoreTop.current){
      list.current.scrollTop=restoreTop.current.top+list.current.scrollHeight-restoreTop.current.height;restoreTop.current=null;
    }else if(atBottom.current)list.current.scrollTop=list.current.scrollHeight;
  },[posts,history]);
  async function loadOlder(){
    if(loading||!displayed[0])return;
    setLoading(true);
    try {
      const data=await request<ChatPost[]>(route+"?before="+encodeURIComponent(position(displayed[0])));
      if(list.current) restoreTop.current={height:list.current.scrollHeight,top:list.current.scrollTop};
      setHistory(old=>[...old,...data]);setMore(data.length===40);
    } catch(e){setNotice((e as Error).message);}
    finally{setLoading(false);}
  }
  const roster=rosters.get(topic.id)?.members??[];
  const links=groupLinks(snapshot,topic);
  const presence=new Map<string,ChatPresence>(snapshot?.windows.map(({agent,activity})=>[agent.id,{activity}]));
  for (const remote of snapshot?.federation?.remotes ?? []) {
    if (remote.topicId===topic.id && remote.status==="approved") {
      const online=remoteOnline(remote);
      presence.set(remote.id,{activity:online?"idle":"offline",label:online?"在线":"离线"});
    }
  }
  const pending=new Set(snapshot?.federation?.grants.filter(grant=>grant.status==="pending"&&roster.some(w=>w.agent.id===grant.windowId)).map(g=>g.id)??[]);
  const groupApplications=(snapshot?.federation?.groups??[]).filter(group=>group.topic===topic.id&&!group.requester&&group.status==="pending");
  for(const person of approvedGroupRecipients(snapshot?.federation?.groups,topic.id))
    if(person.engine)presence.set(person.key,{activity:person.online?"idle":"offline",label:person.online?"在线":"离线"});
  const targets=chatTargets(snapshot,topic,links);
  return <section id="group-chat" aria-label="小组群聊">
    <header className="chat-heading">
      <div className="chat-heading-main">
        <div className="chat-heading-title" data-activity={topic.activity}>
          <span id="chat-anchor" className="nav-dot" aria-hidden="true"/>
          <NavigationName className="current-topic-title" title={topic.name} onRename={()=>navigation.editTopic(topic.id)}>{topic.name}</NavigationName>
        </div>
      </div>
      {groupApplications.length>0&&<button type="button" className="text-button"
        onClick={()=>openGroupCollaboration(groupApplications[0].id)}>待确认的合作申请 {groupApplications.length}</button>}
    </header>
    <ChatThread topic={topic.id} posts={displayed} busy={loading} hasOlder={more&&displayed.length>=40} targets={targets}
      loadOlder={()=>void loadOlder()} pending={pending} members={new Set(roster.map(w=>w.agent.id))} presence={presence} listRef={list} notice={setNotice}
      onScroll={()=>{const el=list.current;if(!el)return;atBottom.current=el.scrollHeight-el.scrollTop-el.clientHeight<48;if(atBottom.current)setUnread(0);}}/>
    {unread>0&&<button type="button" className="chat-jump button compact" onClick={bottom}>{unread} 条新消息 ↓</button>}
    <p className="chat-notice" role="status" hidden={!notice}>{notice}<button type="button" className="text-button" onClick={()=>setNotice("")}>知道了</button></p>
    <ChatComposer topic={topic.id} targets={targets} disabled={topic.status!=="active"} draft={draft}
      update={value=>{drafts.set(topic.id,value);setDraft(value);}} sent={()=>{bottom();void sync().catch(e=>setNotice(e.message));}}/>
  </section>;
}
