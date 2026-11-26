import {useSyncExternalStore} from "react";

interface QuotaLabel {value:string; title:string; loading:boolean}
const labels=new Map<string,QuotaLabel>();
const listeners=new Set<()=>void>();
function subscribe(listener:()=>void) {listeners.add(listener);return ()=>{listeners.delete(listener);};}
export function useEngineQuota(engine:string) {
  return useSyncExternalStore(subscribe,()=>labels.get(engine));
}
export function publishEngineQuota(engine:string,value:string,title:string,loading:boolean) {
  const old=labels.get(engine);
  if (old?.value===value && old.title===title && old.loading===loading) return;
  labels.set(engine,{value,title,loading});
  listeners.forEach(listener=>listener());
}
