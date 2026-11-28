import { useState } from "react";
import { activityLabels, navigation } from "../../services/navigation";
import { copyText } from "../../services/clipboard";
import { WorkspaceIcon } from "../layout/WorkspaceIcon";
import { viewApplication } from "../federation/Federation";
import { deliveryLabels, workLabels, type Attachment, type ChatPost, type ChatPresence, type ChatTarget } from "./chatModel";
import { ChatMarkedText,findChatTextMarks } from "./ChatMarkedText";

const raster = /\.(png|jpe?g|gif|webp|bmp|avif)$/i;
const attachmentRoute = (topic:string,post:string,key:string) =>
  "/topics/"+topic+"/chat/"+encodeURIComponent(post)+"/attachments/"+encodeURIComponent(key);
const time = (value:string) => new Date(value).toLocaleTimeString([], {hour:"2-digit",minute:"2-digit"});

function Receipts({post}:{post:ChatPost}) {
  const [open,setOpen]=useState(false);
  const received=post.receipts.filter(r=>r.delivery==="delivered"&&!r.acknowledged).length;
  if (!post.receipts.length) return null;
  const done=post.receipts.filter(r=>r.work==="completed"||r.work==="replied").length;
  const pending=post.receipts.some(r=>["pending","writing","sending","accepted"].includes(r.delivery)&&!r.acknowledged);
  const summary = post.receipts.length===1 && !received
    ? (post.receipts[0].acknowledged?"已忽略":deliveryLabels[post.receipts[0].delivery]??"投递失败")
    : received===0 && pending ? "排队中" : "已接收 "+received+"/"+post.receipts.length;
  const failed=post.receipts.filter(r=>r.acknowledged || !["pending","writing","sending","accepted","delivered","sent"].includes(r.delivery)).length;
  return <div className="chat-receipts">
    <button className="text-button chat-receipt-toggle" type="button" aria-expanded={open} onClick={()=>setOpen(v=>!v)}>
      <span className="chat-receipt-count" key={received} aria-live="polite" data-received={received>0}>{received>0?"✓ ":""}{summary}</span>
      {done>0 && <span> · 已完成 {done}/{post.receipts.length}</span>}
      {failed>0 && post.receipts.length>1 && <span> · {failed} 条需核对</span>}
      <span aria-hidden="true">{open?" ▴":" ▾"}</span>
    </button>
    {open && <ul>{post.receipts.map(receipt=><li key={receipt.id}><strong>{receipt.name}</strong>
      <span data-delivered={receipt.delivery==="delivered"&&!receipt.acknowledged}>
        {receipt.acknowledged?"已忽略":deliveryLabels[receipt.delivery]??"投递失败"}
        {receipt.work && workLabels[receipt.work]?" · "+workLabels[receipt.work]:""}
      </span></li>)}</ul>}
  </div>;
}

export function ChatThread({topic, posts, busy, loadOlder, hasOlder, pending, members, presence, targets, onScroll, listRef, notice}:{
  topic:string; posts:ChatPost[]; busy:boolean; loadOlder:()=>void; hasOlder:boolean; pending:Set<string>; members:Set<string>;
  presence:Map<string,ChatPresence>;
  targets:ChatTarget[];
  onScroll:()=>void; listRef:React.RefObject<HTMLDivElement|null>; notice:(message:string)=>void;
}) {
  const quoted=new Map(posts.map(post=>[post.id,post]));
  const names=new Map(targets.map(target=>[target.key,target.name]));
  const references=targets.filter(target=>target.kind==="agent").map(target=>"#"+target.name.slice(1));
  for(const post of posts) for(const receipt of post.receipts) quoted.set(receipt.id,post);
  function locate(post:ChatPost,file:Attachment) {
    if(file.project && file.path) window.dispatchEvent(new CustomEvent("ff:locate-file",{detail:{project:file.project,path:file.path}}));
    else window.dispatchEvent(new CustomEvent("ff:open-chat-files",{detail:{
      selected:file.key,files:post.attachments.map(f=>({name:f.name,key:f.key,route:attachmentRoute(topic,post.id,f.key)}))
    }}));
  }
  return <div className="chat-thread" aria-label="群聊消息" ref={listRef} onScroll={onScroll} aria-busy={busy}>
    {hasOlder&&<button className="chat-history text-button" type="button" onClick={loadOlder} disabled={busy}>查看更早消息</button>}
    {!posts.length&&!busy&&<p className="chat-empty">可以发消息，也可以 @ 组员分派任务。</p>}
    {posts.map(post=>{
      const mine=post.source==="workbench-user" && post.kind!=="human-in" || post.source==="peer-mailbox"&&post.kind==="outgoing";
      const system=!!post.application || ["audit","blocked","permission"].includes(post.kind??"");
      const reply=post.replyTo && quoted.get(post.replyTo);
      const sender=post.kind==="human-in" ? undefined : presence.get(post.source);
      const status=sender&&(sender.label??activityLabels[sender.activity]);
      const recipients=post.recipients??post.receipts.map(r=>r.name);
      const destination=recipients.length?recipients.join("、"):reply?reply.sender:post.source==="workbench-user"&&!system?"群聊":"";
      return <article className="chat-message" key={post.id} data-message={post.id} data-mine={mine} data-system={system}>
        <div className="chat-message-body">
          <header>{sender&&<span className="chat-sender-status" data-activity={sender.activity} title={status} role="img" aria-label={status}><span className="nav-dot"/></span>}
            <strong>{post.sender}</strong>{destination&&<span className="chat-destination" title={"发给 "+destination}>
              → {destination}</span>}
            {post.topic!==topic&&<small>关联小组</small>}
            <time dateTime={post.createdAt}>{time(post.createdAt)}</time>
            <div className="chat-message-actions">
              {members.has(post.source)&&<button className="text-button" type="button" title="查看终端" aria-label={"查看 "+post.sender+" 的终端"}
                onClick={()=>navigation.selectAgent(topic,post.source)}><WorkspaceIcon name="terminal"/></button>}
              <button className="text-button" type="button" title="复制消息" aria-label="复制消息"
                onClick={()=>void copyText(post.text).then(()=>notice("已复制消息"),e=>notice(e.message))}>复制</button>
            </div>
          </header>
          {reply&&<button className="chat-quote" type="button" onClick={()=>listRef.current?.querySelector(
            '[data-message="'+CSS.escape(reply.id)+'"]')?.scrollIntoView({block:"center"})}>回复 {reply.sender} · {reply.text}</button>}
          <div className="chat-text"><ChatMarkedText text={post.text} marks={findChatTextMarks(post.text,
            post.receipts.flatMap(receipt=>[names.get(receipt.target)??"@"+receipt.name,"@"+receipt.name]),references)}/></div>
          {post.application && pending.has(post.application) && <button className="button compact" type="button"
            onClick={()=>viewApplication(post.application!)}>查看申请</button>}
          {post.attachments.length>0&&<div className="chat-attachments">{post.attachments.map(file=>{
            const route=attachmentRoute(topic,post.id,file.key);
            return <div className="chat-attachment" key={file.key}>
              {raster.test(file.name)&&<button type="button" className="chat-image" aria-label={"预览 "+file.name}
                onClick={()=>locate(post,file)}><img loading="lazy" src={"/api"+route+"?view=inline"} alt={file.name}/></button>}
              <button type="button" className="chat-attachment-name" title={file.path??file.name}
                onClick={()=>locate(post,file)}><WorkspaceIcon name="docs"/><span>{file.name}</span></button>
              <div className="chat-attachment-tools"><a download={file.name} href={"/api"+route}>下载</a></div>
            </div>;
          })}</div>}
          <Receipts post={post}/>
        </div>
      </article>;
    })}
  </div>;
}
