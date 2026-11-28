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
  const rail = element.closest("#project-tabs");
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
  return visible(document.querySelector(".chat-heading-title")) ?? visible(document.getElementById("return-to-chat"));
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

function flyMessage(entry) {
  if (entry.topic!==selectedTopicId) return;
  const targets=entry.receipts?.map(receipt=>receipt.target)??[];
  const source=entry.source;
  const sourceTopic=topicOfAgent(source)??entry.topic;
  if (entry.replyTopic || sourceTopic!==entry.topic || targets.some(id=>{
    const topic=topicOfAgent(id);
    return topic && topic!==entry.topic;
  })) return;
  const external=collaborationFor(source,entry.topic);
  if (!targets.length && (entry.kind==="human-in" || entry.replyTo || external)) targets.push("workbench-user");
  if (!targets.length) return;
  void fly(messageAnchor(source,entry.topic,targets[0]),targets.map(id=>messageAnchor(id,entry.topic,source)));
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
    if (!posts.has(key) && posts.size===4) continue;
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
        return messageAnchor(route.target,route.targetTopic,route.source);
      });
      void fly(from,targets);
    }
  });
}

async function fly(from, anchors) {
  if (document.visibilityState !== "visible" || !selectedTopicId) return;
  const targets = [...new Set(anchors.filter(Boolean))];
  const target = targets.find(item=>item!==from);
  const flash = () => targets.forEach(item=>item.animate([
    {boxShadow:"inset 0 0 0 1px #7dd3fc, 0 0 9px #38bdf899"},
    {boxShadow:"inset 0 0 0 1px transparent, 0 0 0 transparent"}
  ], {duration:320,easing:"ease-out"}));
  if (!from || !target || reducedMotion.matches) { flash(); return; }
  const center = element => {
    const box=element.getBoundingClientRect();
    return {x:box.x+box.width/2,y:box.y+box.height/2};
  };
  const a=center(from), b=center(target), height=Math.min(29,Math.max(11,Math.abs(a.x-b.x)/9));
  const envelope = sprite("chat-envelope-flight","0 0 24 18",
    '<rect x="1" y="1" width="22" height="16" rx="3"/><path d="m2 3 10 7L22 3"/>');
  const hand = sprite("chat-flick-hand","0 0 28 28",
    '<path fill="#1279bc" d="m2 19 5-6 3-4q1-2 3-1l2 1 6-5q2-2 3 0 1 1-1 3l-7 6 1 4-2 3q-2 2-5 2l-3 4z"/>' +
    '<path fill="#38bdf8" d="M12 15q3-2 6-2l4-3q2-1 3 1t-2 3l-5 4-5 1"/><path fill="#12426b" d="m1 20 3-3 7 7-3 3z"/>');
  const point=(x,y,angle)=>"translate("+Math.round(x-9)+"px,"+Math.round(y-6)+"px) rotate("+angle+"deg)";
  const current = selectedTopicId;
  const side = b.x < a.x ? -1 : 1;
  const pose = (offset,angle) => point(a.x+offset*side,a.y+9,angle*side)+" scaleX("+side+")";
  document.body.append(envelope,hand);
  try {
    hand.animate([
      {transform:pose(-7,12),opacity:0},
      {offset:.45,transform:pose(-3,12),opacity:1},
      {offset:.74,transform:pose(2,-15),opacity:1},
      {transform:pose(5,-20),opacity:0}
    ],{duration:490,easing:"ease-in-out",fill:"both"});
    await envelope.animate([
      {transform:point(a.x,a.y,-8),opacity:0},
      {offset:.31,transform:point(a.x-3*side,a.y,-14),opacity:1},
      {offset:.68,transform:point((a.x+b.x)/2,Math.max(8,(a.y+b.y)/2-height),12),opacity:1},
      {transform:point(b.x,b.y,0),opacity:.35}
    ],{duration:790,easing:"ease-in-out",fill:"both"}).finished;
    if(current===selectedTopicId) flash();
  } finally { envelope.remove();hand.remove(); }
}

window.addEventListener("ff:chat-flight", event => void flyMessage(event.detail));
export { animateGroupHandoffs, flyMessage, messageAnchor };
