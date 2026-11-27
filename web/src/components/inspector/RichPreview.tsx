import { lazy, Suspense, useState, useCallback } from "react";
import type { FileView } from "./workspaceFiles";

const OfficePreview=lazy(()=>import("./OfficePreview"));
export default function RichPreview({file,url}:{file:FileView;url:string}) {
  if(file.kind==="pdf") return <iframe className="document-frame" title={"PDF · "+file.path} src={url}/>;
  if(file.kind==="excel" || file.kind==="ppt") return <Suspense fallback={<p className="inspector-empty">正在加载文档…</p>}><OfficePreview kind={file.kind} url={url} path={file.path}/></Suspense>;
  if(file.kind==="image") return <ImagePreview file={file} url={url}/>;
  if(file.kind==="video") return <VideoPreview path={file.path} url={url}/>;
  return <p className="inspector-empty">{file.kind==="legacy-ppt"?"请将旧版 .ppt 另存为 .pptx 后预览":file.kind==="large"?"文件超过预览上限（文本 512 KiB，图片与办公文档 20 MiB，视频 100 MiB）":"此文件格式暂不支持预览"}</p>;
}

function VideoPreview({path,url}:{path:string;url:string}) {
  const [failed,setFailed]=useState(false);
  return failed ? <p className="inspector-empty">浏览器无法播放此视频，请检查文件编码。</p> :
    <video className="workspace-video" controls preload="metadata" src={url} aria-label={path} onError={()=>setFailed(true)}/>;
}
function ImagePreview({file,url}:{file:FileView;url:string}) {
  const [scale,setScale]=useState<number|null>(null),[turn,rotate]=useState(0),[dims,measure]=useState([0,0]);
  const [area,setArea]=useState([0,0]);
  const [observer]=useState(()=>new ResizeObserver(([entry])=>setArea([entry.contentRect.width,entry.contentRect.height])));
  const watch=useCallback((el:HTMLDivElement|null)=>{if(el)observer.observe(el);return ()=>observer.disconnect();},[observer]);
  const [width,height]=dims, odd=turn%2===1, w=odd?height:width, h=odd?width:height;
  const zoom=scale??(w&&h&&area[0]&&area[1]?Math.min(1,area[0]/w,area[1]/h):1);
  const step=(factor:number)=>setScale(Math.max(.05,Math.min(8,zoom*factor)));
  return <section className="rich-preview">
    <div className="preview-tools"><button aria-label="缩小图片" onClick={()=>step(.8)}>−</button><button onClick={()=>setScale(1)}>100%</button>
    <button aria-label="放大图片" onClick={()=>step(1.25)}>＋</button><button onClick={()=>setScale(null)}>适应窗口</button><button onClick={()=>rotate(n=>(n+1)%4)}>旋转 90°</button>
    <small>{width} × {height} · {Math.round(zoom*100)}% · {(file.size/1024).toFixed(1)} KiB</small></div>
    <div className="image-canvas" ref={watch} onWheel={e=>{if(!e.deltaY)return;step(e.deltaY<0?1.1:1/1.1);}}>
      <div className="image-box" style={{width:w*zoom,height:h*zoom}}><img src={url} alt={file.path}
        style={{width:width?width*zoom:undefined,height:height?height*zoom:undefined,transform:"translate(-50%, -50%) rotate("+turn*90+"deg)"}}
        onLoad={e=>measure([e.currentTarget.naturalWidth,e.currentTarget.naturalHeight])}/></div>
    </div></section>;
}
