import Markdown from "react-markdown";
import remarkGfm from "remark-gfm";

export function MarkdownBody({content, onTaskRecords}:{content:string; onTaskRecords?:()=>void}) {
  return <Markdown skipHtml remarkPlugins={[remarkGfm]} components={{
      a:({href,children})=>href==="records/" && onTaskRecords ?
        <a href="#topic-task-records" onClick={event=>{event.preventDefault();onTaskRecords();}}>{children}</a> :
        href && /^https?:\/\//i.test(href)?<a href={href} target="_blank" rel="noopener noreferrer">{children}</a>:<span>{children}</span>,
      img:({alt})=><span>{alt}</span>
    }}>{content}</Markdown>;
}
