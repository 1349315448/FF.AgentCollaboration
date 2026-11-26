import {layout,useWorkbench} from "../../store/workbenchStore";

export function ReturnAgentSelect({topicId,value,onChange}:{topicId?:string;value?:string;onChange?:(agent:string)=>void}) {
  const {snapshot,selectedTopicId}=useWorkbench();
  const agents=snapshot?.windows.map(w=>w.agent).filter(a=>a.topicId===(topicId??selectedTopicId))??[];
  const initial=agents.find(a=>a.id===layout?.activeWindowId)?.id??(agents.length===1?agents[0].id:"");
  const selection=onChange?{value,onChange:(e:React.ChangeEvent<HTMLSelectElement>)=>onChange(e.target.value)}:{defaultValue:initial};
  return <label>回信 Agent（对方可 @）<select name="contactAgent" {...selection}>
    <option value="">仅接收原任务回信</option>
    {agents.map(a=><option key={a.id} value={a.id}>@{a.name}</option>)}
  </select></label>;
}
