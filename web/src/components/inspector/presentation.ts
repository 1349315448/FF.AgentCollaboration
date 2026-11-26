import { unzipSync, strFromU8 } from "fflate";
type Block={type:"title"|"text";text:string}|{type:"table";rows:string[][]}|{type:"image";src:string;alt:string};
export interface Slide {blocks:Block[]}
const relationship="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
const strictRelationship="http://purl.oclc.org/ooxml/officeDocument/relationships";
const relation=(el:Element|undefined,key:string)=>el?.getAttributeNS(relationship,key)??el?.getAttributeNS(strictRelationship,key)??"";
const elements=(node:Document|Element,name:string)=>Array.from(node.getElementsByTagNameNS("*",name));
export function openPresentation(data:ArrayBuffer, urls:string[]){
  let size=0, count=0;
  const input=new Uint8Array(data), allowed=new Set<string>();
  unzipSync(input,{filter:entry=>{
    if(++count>8192)throw Error("演示文稿包含过多条目");
    if(!/^ppt\/(?:presentation\.xml|_rels\/presentation\.xml\.rels|slides\/(?:slide\d+\.xml|_rels\/slide\d+\.xml\.rels)|media\/[^/]+\.(?:png|jpe?g|gif|webp))$/i.test(entry.name))return false;
    size+=entry.originalSize;
    if((/\.(?:xml|rels)$/i.test(entry.name) && entry.originalSize>4*1024*1024)||size>64*1024*1024)throw Error("演示文稿解压后过大，无法预览");
    allowed.add(entry.name); return false;
  }});
  function bytes(path:string){return allowed.has(path)?unzipSync(input,{filter:entry=>entry.name===path})[path]:undefined;}
  function xml(path:string){
    const part=bytes(path);if(!part)return null;
    if(part.length>4*1024*1024)throw Error("单页内容过大，无法预览");
    const doc=new DOMParser().parseFromString(strFromU8(part),"application/xml");
    if(doc.doctype || doc.getElementsByTagName("parsererror").length)throw Error("幻灯片 XML 格式无效");
    return doc;
  }
  function rels(path:string){
    const dir=path.slice(0,path.lastIndexOf("/")+1), name=path.slice(dir.length);
    const doc=xml(dir+"_rels/"+name+".rels"), links=new Map<string,string>();
    if(doc)for(const r of elements(doc,"Relationship")){
      const id=r.getAttribute("Id"), dest=r.getAttribute("Target");
      if(!id||!dest||r.getAttribute("TargetMode")==="External")continue;
      const resolved=new URL(dest,"https://package.invalid/"+path);
      if(resolved.origin==="https://package.invalid")links.set(id,decodeURIComponent(resolved.pathname.slice(1)));
    }
    return links;
  }
  const manifest=xml("ppt/presentation.xml"), index=rels("ppt/presentation.xml");
  if(!manifest)throw Error("缺少幻灯片目录");
  const media=new Map<string,string>();
  const slides=elements(manifest,"sldId");
  return {length:slides.length, read(page:number):Slide {
    const path=index.get(relation(slides[page],"id"));
    if(!path||!/^ppt\/slides\/slide\d+\.xml$/i.test(path))throw Error("幻灯片引用无效");
    const doc=xml(path), links=rels(path), blocks:Block[]=[];
    if(!doc)throw Error("幻灯片内容缺失");
    const shapes=doc.getElementsByTagName("*");
    if(shapes.length>15000)throw Error("本页元素过多，无法预览");
    for(const shape of Array.from(shapes)){
      if(shape.localName==="sp"){
        const title=["title","ctrTitle"].includes(elements(shape,"ph")[0]?.getAttribute("type")??"");
        for(const para of elements(shape,"p")){
          const text=elements(para,"t").map(t=>t.textContent).join("");
          if(text)blocks.push({type:title?"title":"text",text});
        }
      }else if(shape.localName==="tbl"){
        blocks.push({type:"table",rows:elements(shape,"tr").map(row=>elements(row,"tc").map(cell=>elements(cell,"t").map(t=>t.textContent).join("")))});
      }else if(shape.localName==="pic"){
        const key=links.get(relation(elements(shape,"blip")[0],"embed"));
        if(!key||!/^ppt\/media\/.+\.(?:png|jpe?g|gif|webp)$/i.test(key)||!allowed.has(key))continue;
        if(!media.has(key)){
          const format=key.split(".").pop()!.toLowerCase().replace("jpg","jpeg");
          const src=URL.createObjectURL(new Blob([new Uint8Array(bytes(key)!)],{type:"image/"+format}));
          media.set(key,src);urls.push(src);
        }
        blocks.push({type:"image",src:media.get(key)!,alt:elements(shape,"cNvPr")[0]?.getAttribute("descr")??"幻灯片图片"});
      }
    }
    return {blocks};
  }};
}
