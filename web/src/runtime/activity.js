import { projectKey } from "../services/navigation";
import { selectedTopicId, snapshot } from "../store/workbenchStore";

const breatheAnimations = new Set(["agent-breathe", "idle-breathe", "waiting-breathe"]);
// 话题列表重绘或切换布局时，新出现的灯也从当前页面的同一呼吸相位开始。
document.addEventListener("animationstart", event => {
  if (!breatheAnimations.has(event.animationName)) return;
  const pulse = event.target.getAnimations().find(animation => animation.animationName === event.animationName);
  if (pulse && pulse.startTime !== 0) pulse.startTime = 0;
});

const svgNamespace = "http://www.w3.org/2000/svg";
const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)");

function sprite(className, viewBox, drawing) {
  const element = document.createElementNS(svgNamespace, "svg");
  element.setAttribute("class", className);
  element.setAttribute("viewBox", viewBox);
  element.setAttribute("aria-hidden", "true");
  element.innerHTML = drawing;
  return element;
}

function isVisible(element) {
  if (!element?.getClientRects().length || getComputedStyle(element).visibility !== "visible") return false;
  const rail = element.closest("#project-tabs,.chat-thread");
  if (!rail) return true;
  const bounds = rail.getBoundingClientRect(), row = element.getBoundingClientRect();
  return row.bottom > bounds.top && row.top < bounds.bottom;
}

function visible(element) { return isVisible(element) ? element : null; }
function groupAnchor(topic) {
  return visible(document.querySelector('.topic-choice[data-topic="'+CSS.escape(topic.id)+'"]')) ??
    visible(document.querySelector('.project-button[data-key="'+CSS.escape(projectKey(topic.project))+'"]'));
}
function chatAnchor() {
  return visible(document.getElementById("group-chat-input")) ?? visible(document.getElementById("return-to-chat"));
}
function anchorInTree(item) {
  for (let node=item;node?.closest("#project-tabs");node=node.parentElement) {
    const anchor=node.matches("details") ? node.querySelector(":scope > summary") :
      node.matches("[hidden].tree-loans") ? node.parentElement.querySelector(".tree-cooperation-toggle") :
      node.matches("[data-loan-link]") ? node.querySelector(":scope > .tree-loan-group") :
      node===item ? node : null;
    if (isVisible(anchor)) return anchor;
  }
  return null;
}

function collaborationFor(id, topicId) {
  return snapshot?.federation?.groups?.find(group=>group.topic===topicId &&
    group.recipients.some(member=>member.key===id));
}

function messageAnchor(id, topicId=selectedTopicId, owner) {
  const topic=snapshot?.topics.find(item=>item.id===topicId);
  const fallback=()=>topic && groupAnchor(topic);
  if (!id || id==="workbench-user" || id==="peer-mailbox")
    return topicId===selectedTopicId ? chatAnchor() ?? fallback() : fallback();
  const root=document.querySelector('[data-nav-topic="'+CSS.escape(topicId??"")+'"]');
  if (!root) return fallback();
  const safe=CSS.escape(id);
  const rows=[...root.querySelectorAll('[data-agent="'+safe+'"],[data-remote-id="'+safe+'"]')];
  const ownRow=rows.find(row=>row.closest("[data-cooperation-owner]")?.dataset.cooperationOwner===owner);
  const group=collaborationFor(id,topicId);
  const branch=group && root.querySelector('[data-collab-branch="'+CSS.escape(group.id)+'"]');
  const loan=group && root.querySelector('[data-loan-link="'+CSS.escape(group.id)+'"]');
  return anchorInTree(ownRow ?? rows[0] ?? branch ?? loan) ?? fallback();
}

function topicOfAgent(id) {
  return snapshot?.windows.find(view=>view.agent.id===id)?.agent.topicId;
}

function chatDeliveryAnchor(id,topic,owner) {
  if (id==="workbench-user" && topic===selectedTopicId && isVisible(document.getElementById("group-chat")))
    return document.getElementById("group-chat");
  return messageAnchor(id,topic,owner);
}

function flyMessage(entry) {
  if (entry.topic!==selectedTopicId) return;
  const source=entry.source;
  // 跨组路线由全局快照处理。同一条多收件人消息仍可包含需要在本组播放的路线。
  if (entry.replyTopic || (topicOfAgent(source)??entry.topic)!==entry.topic) return;
  let targets=entry.receipts?.map(receipt=>receipt.target)??[];
  if (!targets.length) {
    targets=(entry.recipients??[]).map(name=>name==="工作台用户" ? "workbench-user" :
      snapshot?.windows.find(w=>w.agent.topicId===entry.topic && w.agent.name===name)?.agent.id).filter(Boolean);
    if (!targets.length && (entry.kind==="human-in" || entry.replyTo ||
        (entry.kind==="post" && source!=="workbench-user"))) targets.push("workbench-user");
  }
  targets=targets.filter(id=>{
    const topic=topicOfAgent(id);
    return !topic || topic===entry.topic;
  });
  if (!targets.length) return;
  return fly(messageAnchor(source,entry.topic,targets[0]),targets.map(id=>chatDeliveryAnchor(id,entry.topic,source)));
}

function animateGroupHandoffs(data, previous) {
  if (!previous || !data.recentHandoffs?.length) return;
  const seen=new Set(previous.map(route=>route.id));
  const topics=new Map(data.topics.map(topic=>[topic.id,topic]));
  const posts=new Map();
  for (const route of data.recentHandoffs) {
    if (seen.has(route.id) || route.sourceTopic===route.targetTopic) continue;
    const source=topics.get(route.sourceTopic),target=topics.get(route.targetTopic);
    if (!source || !target || projectKey(source.project)!==projectKey(target.project)) continue;
    const key=route.post??route.id;
    const routes=posts.get(key)??[];
    routes.push(route);
    posts.set(key,routes);
  }
  if (posts.size) requestAnimationFrame(()=>{
    for (const routes of posts.values()) {
      const first=routes[0];
      const from=messageAnchor(first.source,first.sourceTopic);
      const targets=routes.map(route=>{
        if (route.sourceTopic===selectedTopicId && route.target!=="workbench-user") {
          const partner=messageAnchor(route.target,route.sourceTopic,route.source);
          if (partner && partner!==groupAnchor(topics.get(route.sourceTopic))) return partner;
        }
        return chatDeliveryAnchor(route.target,route.targetTopic,route.source);
      });
      void fly(from,targets);
    }
  });
}

function center(element) {
  const box=element.getBoundingClientRect();
  return {x:box.x+box.width/2,y:box.y+box.height/2};
}

function landingPoint(target, from) {
  if (target.id!=="group-chat") return center(target);
  const {left,right,top,bottom}=target.getBoundingClientRect();
  const x=Math.max(left+12,Math.min(right-12,from.x));
  const y=Math.max(top+12,Math.min(bottom-12,from.y));
  return [{x:left,y},{x:right,y},{x,y:top},{x,y:bottom}]
    .sort((a,b)=>Math.hypot(a.x-from.x,a.y-from.y)-Math.hypot(b.x-from.x,b.y-from.y))[0];
}

function flightPath(a,b,rail) {
  const start="M "+a.x+" "+a.y, end=b.x+" "+b.y;
  if (rail) {
    // 两个树节点通常在同一列，向群聊一侧绕出再落回收件行。
    const bend=Math.min(innerWidth-12,rail.getBoundingClientRect().right+Math.min(190,100+Math.abs(b.y-a.y)*.45));
    return start+" C "+bend+" "+Math.max(8,a.y-30)+" "+bend+" "+Math.min(innerHeight-8,b.y+30)+" "+end;
  }
  return start+" Q "+(a.x+b.x)/2+" "+Math.max(8,(a.y+b.y)/2-48)+" "+end;
}

function flashRecipient(item) {
  item.animate([
    {boxShadow:"inset 0 0 0 1px #7dd3fc, 0 0 9px #38bdf899"},
    {boxShadow:"inset 0 0 0 1px transparent, 0 0 0 transparent"}
  ], {duration:450,easing:"ease-out"});
}

function envelopeFrames(path) {
  const length=path.getTotalLength();
  const start=path.getPointAtLength(0), end=path.getPointAtLength(length);
  const turns=Math.max(1,Math.min(5,Math.round(length/300)));
  const degrees=(end.x>=start.x?1:-1)*turns*360;
  return Array.from({length:25},(_,i)=>{
    const progress=i/24, p=path.getPointAtLength(length*progress);
    const rotation=Math.min(1,progress/.92)*degrees;
    return {transform:"translate("+Math.round(p.x-9)+"px,"+Math.round(p.y-6)+"px) rotate("+rotation+"deg)",
      opacity:Math.min(1,(1-progress)/.08)};
  });
}

async function fly(from, anchors) {
  if (document.visibilityState!=="visible" || !selectedTopicId) return;
  const targets=[...new Set(anchors.filter(Boolean))];
  if (!from || reducedMotion.matches) {targets.forEach(flashRecipient);return;}
  if (!targets.length) return;
  const current=selectedTopicId, a=center(from), rail=from.closest("#project-tabs");
  await Promise.all(targets.map(async target=>{
    if (target===from) {flashRecipient(target);return;}
    let b=landingPoint(target,a);
    const treeRoute=rail && target.closest("#project-tabs")===rail ? rail : null;
    const path=document.createElementNS(svgNamespace,"path");
    path.setAttribute("d",flightPath(a,b,treeRoute));
    const duration=Math.max(1050,Math.min(1850,Math.round(720+path.getTotalLength()*.62)));
    const envelope=sprite("chat-envelope-flight","0 0 24 18",
      '<rect x="1" y="1" width="22" height="16" rx="3"/><path d="m2 3 10 7L22 3"/>');
    document.body.append(envelope);
    const motion=envelope.animate(envelopeFrames(path),{duration,easing:"ease-in-out",fill:"both"});
    // 收件人可能因上线而重排，按旧版的低频校位跟随落点。
    const tracking=setInterval(()=>{
      if (!target.isConnected || current!==selectedTopicId) return;
      const next=landingPoint(target,a);
      if (Math.abs(next.x-b.x)>1 || Math.abs(next.y-b.y)>1) {
        b=next;
        path.setAttribute("d",flightPath(a,b,treeRoute));
        motion.effect.setKeyframes(envelopeFrames(path));
      }
    },120);
    try {
      await motion.finished;
      if (current===selectedTopicId && target.isConnected) flashRecipient(target);
    } finally {clearInterval(tracking);envelope.remove();}
  }));
}

window.addEventListener("ff:chat-flight", event => void flyMessage(event.detail));
export { animateGroupHandoffs, flyMessage, messageAnchor };
