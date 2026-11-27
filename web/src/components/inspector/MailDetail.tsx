import {useEffect,useState} from "react";
import {request} from "../../services/api";
import {copyText} from "../../services/clipboard";
import {useWorkbench} from "../../store/workbenchStore";
import Markdown from "react-markdown";
import remarkGfm from "remark-gfm";
interface Detail {id:string;from:string;target:string;text:string;files:string[];document?:string;returnHint?:string;state:string;acknowledged:boolean}
const states:Record<string,string>={pending:"待接收",writing:"写入中",delivered:"已送达",delivery_uncertain:"待核对"};
export function MailDetail({reference}:{reference:string}){
  const {snapshot}=useWorkbench(),[type,id]=reference.split(":");
  const local=snapshot?.timeline.find(row=>row.id===id);
  const peer=type==="peer"?snapshot?.federation?.mail.find(row=>row.seq===Number(id)):undefined;
  const [peerText,setPeerText]=useState("");
  const [attachments,setAttachments]=useState<{id:string;name:string;bytes:number}[]>([]);
  const [detail,setDetail]=useState<Detail>(),[document,setDocument]=useState(""),[note,status]=useState(""),[busy,lock]=useState(false);
  useEffect(()=>{
    const abort=new AbortController(),opts={signal:abort.signal};
    async function load(){
      if(type==="message")setDetail(await request<Detail>("/messages/"+id,opts));
      else {const result=await request<{text:string}>("/peers/mail/"+id,opts);setPeerText(result.text);}
      setAttachments(peer?.attachmentCount ? await request("/peers/mail/"+id+"/attachments",opts) : []);
      if(peer?.hasDocument){
        const r=await fetch("/api/peers/mail/"+id+"/document",opts);
        if(!r.ok)throw Error("无法读取外部交接文档");setDocument(await r.text());
      }
    }
    load().catch(e=>{if(!abort.signal.aborted)status(e.message);});
    return ()=>abort.abort();
  },[id,type,peer?.hasDocument,peer?.attachmentCount,local?.state,local?.acknowledged]);
  const state=detail?.acknowledged?"已忽略":states[detail?.state??""]??({application:"接入申请",incoming:"外部来信",outgoing:"发往外部","reply-in":"外部回信","reply-out":"回信已发",blocked:"已拦截",audit:"变更告警"} as Record<string,string>)[peer?.kind??""];
  const text=[detail?.text??peerText,...(detail?.files??[]),detail?.returnHint].filter(Boolean).join("\n\n");
  async function action(fn:()=>Promise<unknown>,ok:string){if(busy)return;lock(true);try{await fn();status(ok);}catch(e){status((e as Error).message);}finally{lock(false);}}
  const stamp=local?.createdAt??peer?.createdAt;
  const address=(key:string)=>snapshot?.windows.find(w=>w.agent.id===key)?.agent.name??snapshot?.federation?.remotes.find(r=>r.id===key)?.agentName??(key==="peer-mailbox"?"协作信箱":key);
  const peerRoute=peer?(peer.kind==="incoming"?peer.label:address(peer.source))+" → "+(peer.kind==="reply-out"?peer.label:address(peer.target)):undefined;
  return <section className="mail-detail"><h3>{peerRoute??[local?.from??detail?.from,local?.to].filter(Boolean).join(" → ")}</h3>
    <small>{stamp?new Date(stamp).toLocaleString():""}　{state}</small><p style={{whiteSpace:"pre-wrap"}}>{text}</p>
    {attachments.length>0&&<ul aria-label="协作附件">{attachments.map(file=><li key={file.id}>
      <a href={"/api/peers/mail/"+id+"/attachments/"+file.id} download={file.name}>{file.name}</a>
      <small>　{Math.ceil(file.bytes/1024)} KB</small></li>)}</ul>}
    {document&&<Markdown skipHtml remarkPlugins={[remarkGfm]} components={{img:({alt})=><span>{alt}</span>,a:({children})=><span>{children}</span>}}>{document}</Markdown>}
    <div className="preview-tools"><button disabled={busy||!text} onClick={()=>action(()=>copyText(text),"已复制")}>复制</button>
    {detail&&<button disabled={busy} onClick={()=>action(()=>request("/messages/"+id+"/resend",{method:"POST"}),"已重新投递")}>重发</button>}
    {detail&&!detail.acknowledged&&["pending","delivery_uncertain"].includes(detail.state)&&<button disabled={busy} onClick={()=>action(async()=>{await request("/windows/"+detail.target+"/inbox/"+id+"/acknowledge",{method:"POST"});setDetail({...detail,acknowledged:true});},"已撤回")}>撤回</button>}
    {peer?.kind==="audit"&&<button onClick={()=>window.document.getElementById("inspector-tab-changes")?.click()}>查看变更</button>}</div>
    {note&&<p role="status">{note}</p>}</section>;
}
