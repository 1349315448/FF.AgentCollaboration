import Markdown from "react-markdown";
import remarkGfm from "remark-gfm";

export function MarkdownBody({content}:{content:string}) {
  return <Markdown skipHtml remarkPlugins={[remarkGfm]} components={{
      a:({href,children})=>href && /^https?:\/\//i.test(href)?<a href={href} target="_blank" rel="noopener noreferrer">{children}</a>:<span>{children}</span>,
      img:({alt})=><span>{alt}</span>
    }}>{content}</Markdown>;
}
