import {useWorkbench} from "../../store/workbenchStore";

export function ReturnAgentSelect({topicId,value,onChange}:{topicId?:string;value?:string;onChange?:(agent:string)=>void}) {
  const {snapshot,selectedTopicId}=useWorkbench();
  const leaderId=snapshot?.topics.find(t=>t.id===(topicId??selectedTopicId))?.leaderWindowId;
  const leader=snapshot?.windows.find(w=>w.agent.id===leaderId)?.agent;
  const selection=onChange?{value:value===leader?.id?value:"",onChange:(e:React.ChangeEvent<HTMLSelectElement>)=>onChange(e.target.value)}:{defaultValue:leader?.id??""};
  return <label>回信 Agent（对方可 @）<select name="contactAgent" {...selection}>
    <option value="">仅接收原任务回信</option>
    {leader && <option value={leader.id}>@{leader.name}</option>}
  </select></label>;
}
