import { useEffect, useLayoutEffect, useRef, useState, type FormEvent, type KeyboardEvent } from "react";
import { request } from "../../services/api";
import { activityLabels } from "../../services/navigation";
import { WorkspaceIcon } from "../layout/WorkspaceIcon";
import { type ChatTarget } from "./chatModel";
import { ChatMarkedText } from "./ChatMarkedText";

type Marker = "@"|"#";
interface Mention { start:number; target:ChatTarget; marker:Marker }
const mentionText=(mention:Mention)=>mention.marker+mention.target.name.slice(1);
export interface ChatDraft { text:string; mentions:Mention[]; files:File[] }
export const emptyDraft = ():ChatDraft => ({text:"",mentions:[],files:[]});
const emojis = ["👍","✅","👀","🙌","🎉","💡","🚀","🧪","⚠️","🔧","📝","🙏","😊","🤔","💬","📎"];
const limitBytes = 20 * 1024 * 1024;

function mentionQuery(text:string,cursor:number) {
  const match=text.slice(0,cursor).match(/(?:^|[^A-Za-z0-9._+@#-])([@#])([^\s@#，。！？、；：]*)$/);
  return match&&{marker:match[1] as Marker,term:match[2],start:cursor-match[2].length-1};
}

function replaceText(draft:ChatDraft,start:number,end:number,value:string):ChatDraft {
  const shift=value.length-(end-start);
  const mentions=draft.mentions.flatMap(mention=>{
    if (mention.start+mentionText(mention).length<=start) return [mention];
    if (mention.start>=end) return [{...mention,start:mention.start+shift}];
    return [];
  });
  return {...draft,text:draft.text.slice(0,start)+value+draft.text.slice(end),mentions};
}

export function ChatComposer({topic, targets, draft, update, sent, disabled}:{
   topic:string; targets:ChatTarget[]; draft:ChatDraft;
  update:(d:ChatDraft)=>void; sent:()=>void; disabled:boolean
}) {
  const input = useRef<HTMLTextAreaElement>(null), picker = useRef<HTMLInputElement>(null);
  const highlight = useRef<HTMLDivElement>(null);
  const edit = useRef<{text:string; start:number} | null>(null);
  const compositionStart = useRef<number|null>(null);
  const caret = useRef<number|null>(null);
  const [mode,setMode] = useState<Marker|"emoji"|null>(null);
  const [composing,setComposing]=useState(false);
  const [cursor,setCursor] = useState(0), [active,setActive] = useState(0);
  const [busy,setBusy] = useState(false), [error,setError] = useState("");
  const pickerMarker=mode==="@"||mode==="#"?mode:null;
  const match = mentionQuery(draft.text,cursor);
  const recipients = [...new Map(draft.mentions.filter(mention=>mention.marker==="@").map(({target})=>[target.key,target])).values()];
  const human = recipients.find(choice=>choice.kind==="human");
  const search=match?.marker===pickerMarker?match.term.toLocaleLowerCase():"";
  const matches = targets.filter(choice=>(pickerMarker==="#"?choice.kind==="agent":
    !human&&(!recipients.length||choice.kind!=="human")) &&
    [choice.name,choice.engine,choice.detail].join(" ").toLocaleLowerCase().includes(search));

  useEffect(()=>{
    const field=input.current!;
    // 保留输入发生前的光标位置，避免重复的 @ 名称在删除或粘贴时绑定到另一段文字。
    const remember=(event:Event)=>{
      const inputEvent=event as InputEvent;
      const composing=inputEvent.isComposing ||
        inputEvent.inputType==="insertCompositionText" || inputEvent.inputType==="insertFromComposition";
      edit.current={text:field.value,start:composing?compositionStart.current??field.selectionStart:field.selectionStart};
      if (!composing) compositionStart.current=null;
    };
    field.addEventListener("beforeinput",remember);
    return ()=>field.removeEventListener("beforeinput",remember);
  },[]);
  useLayoutEffect(()=>{
    if (caret.current==null || !input.current) return;
    input.current.focus();
    input.current.setSelectionRange(caret.current,caret.current);
    caret.current=null;
  });

  function write(value:ChatDraft,position:number) {
    caret.current=position;
    edit.current=null;
    setCursor(position);setMode(null);setError("");
    update(value);
  }

  function choose(choice:ChatTarget,marker:Marker,completeQuery=true) {
    const field=input.current;
    if (!field || disabled || busy) return;
    if (marker==="@" && (human || choice.kind==="human" && recipients.length)) return;
    const position=field.selectionStart;
    const query=completeQuery?mentionQuery(draft.text,position):null;
    const start=query?.marker===marker?query.start:position;
    const mention={start,target:choice,marker};
    const value=mentionText(mention)+(/\s/.test(draft.text[field.selectionEnd]??"")?"":" ");
    const next=replaceText(draft,start,field.selectionEnd,value);
    if (next.text.length>field.maxLength) return;
    next.mentions.push(mention);
    write(next,start+value.length);
  }

  function changeText(field:HTMLTextAreaElement) {
    const previous=edit.current;
    edit.current=null;
    const text=field.value, position=field.selectionStart;
    // 一次原生编辑保留选区两边的内容。无法定位时清掉收件绑定，草稿文字仍保留。
    const start=Math.min(previous?.start??0,position);
    const end=draft.text.length-(text.length-position);
    const next=previous?.text===draft.text && end>=start && end<=draft.text.length &&
      draft.text.slice(0,start)===text.slice(0,start) && draft.text.slice(end)===text.slice(position)
      ? replaceText(draft,start,end,text.slice(start,position))
      : {...draft,text,mentions:[]};
    update(next);setCursor(position);setActive(0);
    const query=mentionQuery(text,position);
    setMode(query && !next.mentions.some(mention=>position>mention.start && position<=mention.start+mentionText(mention).length)?query.marker:null);
  }
  function addFiles(files:FileList | File[]) {
    const combined = [...draft.files,...Array.from(files)];
    if (combined.length>20 || combined.reduce((size,file)=>size+file.size,0)>limitBytes) {
      setError("每条消息最多 20 个附件，总大小不超过 20 MiB。");return;
    }
    update({...draft,files:combined});setError("");
  }
  function put(value:string) {
    const start=input.current?.selectionStart??draft.text.length, end=input.current?.selectionEnd??start;
    write(replaceText(draft,start,end,value),start+value.length);
  }
  async function send(event?:FormEvent) {
    event?.preventDefault();
    if (disabled || busy || (!draft.text.trim() && !draft.files.length)) return;
    if (pickerMarker==="@" && match?.marker==="@" && matches.length) {
      setError("请选择要 @ 的收件人。");input.current?.focus();return;
    }
    if (human && draft.files.length) {setError("跨端用户聊天支持文本。项目附件可在本群发送。");return;}
    const payload = {text:draft.text,targets:recipients.filter(t=>t.kind!=="human").map(t=>t.key),humanPeer:human?.key};
    let body:typeof payload | FormData = payload;
    if (draft.files.length) {
      const form = new FormData();
      form.append("message",JSON.stringify(payload));
      draft.files.forEach(file=>form.append("files",file,file.name));
      body=form;
    }
    setBusy(true);setError("");
    try {
      await request("/topics/"+topic+"/chat",{method:"POST",body});
      update(emptyDraft());setMode(null);sent();input.current?.focus();
    } catch(e) {setError((e as Error).message);}
    finally {setBusy(false);}
  }
  function onKey(event:KeyboardEvent<HTMLTextAreaElement>) {
    if (event.nativeEvent.isComposing || event.keyCode===229) return;
    if (event.key==="Enter" && event.shiftKey) return;
    if (mode && event.key==="Escape") {event.preventDefault();setMode(null);return;}
    if (pickerMarker && matches.length && ["ArrowDown","ArrowUp","Enter","Tab"].includes(event.key)) {
      event.preventDefault();
      if (event.key==="Enter" || event.key==="Tab") choose(matches[active]??matches[0],pickerMarker);
      else setActive((active+matches.length+(event.key==="ArrowDown"?1:-1))%matches.length);
    } else if (event.key==="Enter") {
      event.preventDefault();
      if (!event.repeat) void send();
    }
  }
  return <form className="chat-composer" onSubmit={send} onDragOver={event=>event.preventDefault()} onDrop={event=>{
    event.preventDefault();if (!disabled && !busy) addFiles(event.dataTransfer.files);
  }}>
    {draft.files.length>0 && <div className="chat-chips" aria-label="待发送的附件">{draft.files.map((file,index)=>
      <button type="button" key={index} className="chat-chip" disabled={busy} title={"移除 "+file.name} onClick={()=>
        update({...draft,files:draft.files.filter((_,i)=>i!==index)})}><WorkspaceIcon name="docs"/>{file.name} <span aria-hidden="true">×</span></button>)}</div>}
    <div className="chat-editor" data-highlight={draft.mentions.length>0&&!composing}>
      <div className="chat-editor-highlight" ref={highlight} aria-hidden="true">
        <ChatMarkedText text={draft.text} marks={draft.mentions.map(mention=>({start:mention.start,text:mentionText(mention)}))}/>
        {draft.text.endsWith("\n")&&"\u200b"}
      </div>
      <textarea id="group-chat-input" ref={input} rows={3} aria-label="群聊消息" disabled={disabled || busy}
      maxLength={120000} placeholder={disabled?"此小组已归档":"@ 选收件人 · # 引用 Agent"} value={draft.text}
      aria-expanded={!!pickerMarker} aria-controls={pickerMarker?"chat-recipients":undefined}
      onCompositionStart={event=>{compositionStart.current=event.currentTarget.selectionStart;setComposing(true);}}
      onCompositionEnd={()=>setComposing(false)}
      onScroll={event=>{if(highlight.current)highlight.current.scrollTop=event.currentTarget.scrollTop;}}
      onChange={event=>changeText(event.currentTarget)} onSelect={event=>setCursor(event.currentTarget.selectionStart)}
        onPaste={event=>{if(event.clipboardData.files.length){event.preventDefault();addFiles(event.clipboardData.files);}}} onKeyDown={onKey}/>
    </div>
    {pickerMarker && <div id="chat-recipients" className="chat-popover" role="listbox" aria-label={pickerMarker==="@"?"选择收件人":"引用 Agent"}>
      {matches.length ? matches.map((choice,index)=><button type="button" role="option" aria-selected={active===index} key={choice.key}
        className="chat-recipient" data-activity={choice.activity}
        onMouseDown={event=>event.preventDefault()} onMouseEnter={()=>setActive(index)} onClick={()=>choose(choice,pickerMarker)}>
        <span className="chat-recipient-main">
          {choice.activity&&<span className="nav-dot" aria-hidden="true"/>}
          <span title={choice.name}>{pickerMarker+choice.name.slice(1)}</span>
          {(choice.engine||choice.detail)&&<small title={choice.detail??choice.engine}>{choice.engine??choice.detail}</small>}
        </span>
        {choice.activity&&<small className="chat-recipient-status">{choice.status??activityLabels[choice.activity]}</small>}
      </button>) : <p>{pickerMarker==="@"?"没有匹配的收件人":"没有匹配的 Agent"}</p>}
    </div>}
    {mode==="emoji" && <div className="chat-popover chat-emojis" aria-label="表情">
      {emojis.map(emoji=><button key={emoji} type="button" onClick={()=>put(emoji)}>{emoji}</button>)}</div>}
    <div className="chat-composer-tools">
      <button type="button" className="text-button" title="选择收件人" aria-label="选择收件人" disabled={disabled || busy}
        onClick={()=>{setActive(0);setMode(mode==="@"?null:"@");input.current?.focus();}}>@</button>
      <button type="button" className="text-button" title="引用 Agent（只写入正文）" aria-label="引用 Agent" disabled={disabled || busy}
        onClick={()=>{setActive(0);setMode(mode==="#"?null:"#");input.current?.focus();}}>#</button>
      <button type="button" className="text-button" title="选择图片或文件" aria-label="选择图片或文件" disabled={disabled || busy || !!human}
        onClick={()=>picker.current?.click()}><WorkspaceIcon name="docs"/></button>
      <button type="button" className="text-button" title="表情" aria-label="表情" disabled={disabled || busy}
        onClick={()=>setMode(mode==="emoji"?null:"emoji")}>☺</button>
      <input ref={picker} type="file" multiple hidden onChange={e=>{if(e.target.files)addFiles(e.target.files);e.target.value="";}}/>
      <button type="submit" className="button primary chat-send" disabled={disabled || busy || (!draft.text.trim() && !draft.files.length)}
        title="回车发送 · Shift+回车换行">{busy?"发送中…":"发送"}</button>
    </div>
    {error && <p className="form-error" role="alert">{error}</p>}
  </form>;
}
