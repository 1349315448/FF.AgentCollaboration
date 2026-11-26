import { useEffect, useRef, useState } from "react";
import { openPresentation } from "./presentation";
interface Sheet {name:string;rows:string[][];limited:boolean}
type Result={names:string[]}|{slides:ReturnType<typeof openPresentation>};
export default function OfficePreview({kind,url,path}:{kind:"excel"|"ppt";url:string;path:string}) {
  const [result,show]=useState<Result>(),[page,select]=useState(0),[error,fail]=useState("");
  const workerRef=useRef<Worker|null>(null), timeout=useRef<number>(0);
  const [sheet,setSheet]=useState<Sheet>(),[slide,setSlide]=useState<ReturnType<ReturnType<typeof openPresentation>["read"]>>();
  function watchWorker(){clearTimeout(timeout.current); timeout.current=window.setTimeout(()=>{workerRef.current?.terminate();fail("工作簿解析超时，请缩小文件后重试");},15000);}
  useEffect(()=>{
    const controller=new AbortController(), images:string[]=[];
    let worker:Worker|undefined;
    async function open(){
      const response=await fetch(url,{signal:controller.signal});
      if(!response.ok)throw Error("无法读取文档");
      const bytes=await response.arrayBuffer();
      if(controller.signal.aborted)return;
      if(kind==="ppt"){show({slides:openPresentation(bytes,images)});return;}
      worker=new Worker(new URL("./spreadsheet.worker.ts",import.meta.url),{type:"module"});
      workerRef.current=worker;watchWorker();
      worker.onmessage=e=>{clearTimeout(timeout.current);if(e.data.error)fail(e.data.error);
        else if(e.data.names)show({names:e.data.names});else setSheet(e.data.sheet);};
      worker.onerror=()=>{fail("无法解析工作簿");worker?.terminate();};
      worker.postMessage({bytes,csv:path.toLowerCase().endsWith(".csv")},[bytes]);
    }
    open().catch(e=>{if(!controller.signal.aborted)fail(e.message);});
    return ()=>{controller.abort();worker?.terminate();workerRef.current=null;clearTimeout(timeout.current);images.forEach(url=>URL.revokeObjectURL(url));};
  },[url,kind,path]);
  useEffect(()=>{
    if(!result)return;
    if("names" in result){setSheet(undefined);watchWorker();workerRef.current?.postMessage({page});}
    else try{setSlide(result.slides.length?result.slides.read(page):undefined);}catch(e){fail((e as Error).message);}
  },[result,page]);
  if(error || !result)return <p className="inspector-empty" role="status">{error||"正在读取文档…"}</p>;
  if("names" in result){
    if(!result.names.length)return <p>工作簿没有工作表</p>;
    if(!sheet)return <p role="status">正在读取工作表…</p>;
    return <section className="rich-preview"><nav className="preview-tools" aria-label="工作表">{result.names.map((name,i)=><button key={name} aria-pressed={i===page} onClick={()=>select(i)}>{name}</button>)}</nav>
      {sheet.limited&&<small>仅预览前 200 行、50 列；每个工作簿最多显示 32 张工作表。</small>}
      <div className="sheet-scroll"><table><tbody>{sheet.rows.map((row,i)=><tr key={i}>{row.map((cell,j)=>i===0?<th key={j}>{cell}</th>:<td key={j}>{cell}</td>)}</tr>)}</tbody></table></div></section>;
  }
  if(!slide)return <p>没有可预览的幻灯片</p>;
  return <section className="rich-preview"><nav className="preview-tools" aria-label="幻灯片"><button disabled={!page} onClick={()=>select(page-1)}>上一页</button>
    <span>{page+1} / {result.slides.length}</span><button disabled={page+1>=result.slides.length} onClick={()=>select(page+1)}>下一页</button></nav>
    <article className="slide-preview" aria-label="幻灯片内容">{!slide.blocks.length&&<p>本页没有可预览的文字、表格或图片。</p>}{slide.blocks.map((block,i)=>block.type==="image"?<img key={i} src={block.src} alt={block.alt}/>:
      block.type==="table"?<table key={i}><tbody>{block.rows.map((row,j)=><tr key={j}>{row.map((v,k)=><td key={k}>{v}</td>)}</tr>)}</tbody></table>:
      block.type==="title"?<h3 key={i}>{block.text}</h3>:<p key={i}>{block.text}</p>)}</article></section>;
}
