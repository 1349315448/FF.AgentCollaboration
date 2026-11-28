import {Fragment} from "react";

export interface TextMark { start:number; text:string; href?:string }

export function ChatMarkedText({text,marks}:{text:string; marks:TextMark[]}) {
  let end=0;
  const parts=[...marks].sort((a,b)=>a.start-b.start).flatMap(mark=>{
    if(mark.start<end || !text.startsWith(mark.text,mark.start)) return [];
    const before=text.slice(end,mark.start);
    end=mark.start+mark.text.length;
    return <Fragment key={mark.start}>{before}{mark.href
      ?<a href={mark.href} target="_blank" rel="noopener noreferrer">{mark.text}</a>
      :<span className={mark.text.startsWith("@")?"chat-address":"chat-reference"}>{mark.text}</span>}</Fragment>;
  });
  return <>{parts}{text.slice(end)}</>;
}

// 历史正文为纯文本。仅将回执对应的 @ 名称及候选中的 # 名称按正文语法着色。
export function findChatTextMarks(text:string, addresses:string[], references:string[]):TextMark[] {
  const ordered=(names:string[])=>[...new Set(names)].sort((a,b)=>b.length-a.length);
  const at=ordered(addresses), hash=ordered(references);
  const links:TextMark[]=[];
  for(const match of text.matchAll(/https?:\/\/[^\s<>"'«»“”，。！？；、（）【】\x60]+/gi)){
    let value=match[0].replace(/[.,!;:]+$/,"");
    for(const [open,close] of [["(",")"],["[","]"]]){
      let extra=value.split(close).length-value.split(open).length;
      while(extra-->0 && value.endsWith(close)) value=value.slice(0,-1);
    }
    try {
      const url=new URL(value);
      if(!url.username&&!url.password)links.push({start:match.index,text:value,href:url.href});
    } catch { /* Keep malformed addresses as written. */ }
  }
  return links.concat(Array.from(text.matchAll(/(?<![A-Za-z0-9._+@#-])[@#]/g)).flatMap(match=>{
    const start=match.index;
    const value=(match[0]==="@"?at:hash).find(name=>{
      if(!text.startsWith(name,start)) return false;
      const after=text[start+name.length];
      return after===undefined || /[\s,.!?;:，。！？；：、）)\]】@#]/.test(after);
    });
    return value?[{start,text:value}]:[];
  }));
}
