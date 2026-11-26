import { read, utils } from "xlsx";
import { unzipSync } from "fflate";

let input:Uint8Array<ArrayBuffer>, csv:string|undefined, names:string[]=[];
const rows=200, cols=50, maxSheets=32;
function csvPreview(text:string) {
  let delimiter=",", inQuote=false;
  for(let i=0;i<text.length;i++){
    const c=text[i];
    if(c==='"'){if(inQuote&&text[i+1]==='"')i++;else inQuote=!inQuote;}
    if(!inQuote && c==="\t"){delimiter="\t";break;}
    if(!inQuote && (c===","||c==="\r"||c==="\n"))break;
  }
  const table:string[][]=[];let row:string[]=[],field="",quoted=false,limited=false,col=0;
  function cell(){if(col<cols)row.push(field);else limited=true;field="";col++;}
  for(let i=0;i<text.length;i++){
    const c=text[i];
    if(c==='"'){
      if(quoted && text[i+1]==='"'){if(col<cols)field+='"';i++;}else quoted=!quoted;
    }else if(!quoted && c===delimiter)cell();
    else if(!quoted && (c==="\r"||c==="\n")){
      if(c==="\r"&&text[i+1]==="\n")i++;
      cell();table.push(row);row=[];col=0;
      if(table.length===rows){limited ||= i+1<text.length;break;}
    }else if(col<cols)field+=c;
  }
  if(table.length<rows&&(field||row.length)) {cell();table.push(row);}
  return {rows:table,limited};
}
self.onmessage=({data})=>{
  try {
    if(data.bytes){
      input=new Uint8Array(data.bytes);csv=undefined;
      if(data.csv){
        const encoding=input[0]===255&&input[1]===254?"utf-16le":input[0]===254&&input[1]===255?"utf-16be":"utf-8";
        try{csv=new TextDecoder(encoding,{fatal:true}).decode(input);}catch{csv=new TextDecoder("gb18030",{fatal:true}).decode(input);}
        names=["CSV"];
      }else{
        if(input[0]===80&&input[1]===75){
          let expanded=0,count=0;
          unzipSync(input,{filter:entry=>{expanded+=entry.originalSize;
            if(++count>8192||expanded>64*1024*1024)throw Error("工作簿解压后过大，无法预览");return false;}});
        }
        names=read(input,{type:"array",bookSheets:true}).SheetNames;
      }
      self.postMessage({names:names.slice(0,maxSheets)});return;
    }
    const name=names[data.page];if(!name)return;
    if(csv!==undefined){self.postMessage({sheet:{name,...csvPreview(csv)}});return;}
    const workbook=read(input,{type:"array",sheets:[name],dense:true,sheetRows:rows,cellHTML:false,cellFormula:false,cellStyles:false});
    const sheet=workbook.Sheets[name],cells:string[][]=[];
    const range=utils.decode_range(sheet["!fullref"]??sheet["!ref"]??"A1");
    for(let i=0;i<=Math.min(rows-1,range.e.r);i++){
      const row=[];
      for(let j=0;j<=Math.min(cols-1,range.e.c);j++){const cell=sheet["!data"]?.[i]?.[j];row.push(cell?utils.format_cell(cell):"");}
      cells.push(row);
    }
    self.postMessage({sheet:{name,rows:cells,limited:range.e.r>=rows||range.e.c>=cols||names.length>maxSheets}});
  }catch(err){self.postMessage({error:err instanceof Error?err.message:"无法解析工作簿"});}
};
