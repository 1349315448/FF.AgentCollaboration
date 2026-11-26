import { memo, useMemo } from "react";
import hljs from "highlight.js/lib/core";
import csharp from "highlight.js/lib/languages/csharp";
import typescript from "highlight.js/lib/languages/typescript";
import javascript from "highlight.js/lib/languages/javascript";
import json from "highlight.js/lib/languages/json";
import css from "highlight.js/lib/languages/css";
import xml from "highlight.js/lib/languages/xml";
import python from "highlight.js/lib/languages/python";

const grammars={csharp,typescript,javascript,json,css,xml,python};
Object.entries(grammars).forEach(([id,language])=>hljs.registerLanguage(id,language));
const langs:Record<string,string>={cs:"csharp",js:"javascript",jsx:"javascript",mjs:"javascript",ts:"typescript",tsx:"typescript",json:"json",css:"css",html:"xml",xml:"xml",csproj:"xml",py:"python"};
export default memo(function CodeText({path,content}:{path:string;content:string}) {
  const ext=path.split(".").pop()?.toLowerCase()??"";
  const language=Object.hasOwn(langs,ext)?langs[ext]:undefined;
  const html=useMemo(()=>language && content.length<=100000?hljs.highlight(content,{language,ignoreIllegals:true}).value:null,[content,language]);
  const lines=useMemo(()=>Array.from({length:content.split(/\r\n|\r|\n/).length},(_,i)=>i+1).join("\n"),[content]);
  return <div className="source-scroll" tabIndex={0} aria-label="源代码"><pre aria-hidden="true" className="line-numbers">{lines}</pre>
    {html===null?<pre><code>{content}</code></pre>:<pre><code dangerouslySetInnerHTML={{__html:html}}/></pre>}</div>;
});
