import {useEffect, useState} from "react";
import {request} from "../../services/api";
import "./federation.css";

interface IceServer { url:string; username:string|null; hasCredential:boolean }
interface IceDraft { url:string; username:string; credential:string }

interface Settings {
  origin:string|null;
  fingerprint:string|null;
  hasSigningKey:boolean;
  iceServers?:IceServer[];
  bridgeBlockReason?:string|null;
  readError:boolean;
}

const path = "/peers/signaling/settings";

export function SignalingPreferences() {
  const [saved,setSaved] = useState<Settings>();
  const [origin,setOrigin] = useState("");
  const [fingerprint,setFingerprint] = useState("");
  const [key,setKey] = useState("");
  const [removeKey,setRemoveKey] = useState(false);
  const [iceServers,setIceServers] = useState<IceDraft[]>([]);
  const [busy,setBusy] = useState(false);
  const [feedback,setFeedback] = useState("");

  function apply(settings:Settings) {
    setSaved(settings);
    setOrigin(settings.origin ?? "");
    setFingerprint(settings.fingerprint ?? "");
    setKey("");
    setRemoveKey(false);
    setIceServers((settings.iceServers ?? []).map(server=>({url:server.url,username:server.username ?? "",credential:""})));
  }

  useEffect(()=>{
    const dialog = document.querySelector<HTMLDialogElement>("#settings-dialog")!;
    let load:AbortController|undefined;
    function toggled() {
      load?.abort();
      setKey("");
      setRemoveKey(false);
      setIceServers([]);
      if (!dialog.open) return;
      setSaved(undefined);
      setFeedback("");
      const attempt = load = new AbortController();
      void request<Settings>(path,{signal:attempt.signal}).then(value=>{
        if (!attempt.signal.aborted) apply(value);
      }).catch(error=>{if (!attempt.signal.aborted) setFeedback(error.message);});
    }
    dialog.addEventListener("toggle",toggled);
    return ()=>{load?.abort();dialog.removeEventListener("toggle",toggled);};
  },[]);

  async function perform(action:()=>Promise<void>) {
    setBusy(true);
    setFeedback("");
    try { await action(); }
    catch(error) { setFeedback(error instanceof Error ? error.message : "操作失败"); }
    finally { setBusy(false); }
  }

  const configured = Boolean(saved?.origin && saved.fingerprint);
  const savedIce = saved?.iceServers ?? [];
  const iceChanged = iceServers.length !== savedIce.length || iceServers.some((entry,index)=>
    entry.url.trim() !== savedIce[index]?.url || entry.username.trim() !== (savedIce[index]?.username ?? "") || Boolean(entry.credential));
  const dirty = origin.trim() !== (saved?.origin ?? "") || fingerprint.trim() !== (saved?.fingerprint ?? "") || Boolean(key) || removeKey || iceChanged;

  function editIce(index:number, field:keyof IceDraft, value:string) {
    setIceServers(rows=>rows.map((row,i)=>i===index?{...row,credential:"",[field]:value}:row));
  }
  return <section className="fed-fields"><h3>信令服务器</h3>
    <label>服务地址<input type="url" maxLength={512} placeholder="https://203.0.113.10:5190" spellCheck={false}
      value={origin} disabled={!saved||busy} onChange={e=>setOrigin(e.target.value)}/></label>
    <label>公钥指纹<input placeholder="可粘贴服务器控制台的整行指纹" spellCheck={false}
      value={fingerprint} disabled={!saved||busy} onChange={e=>setFingerprint(e.target.value)}/></label>
    <label>签发密钥（仅生成链接的一端填写）<input type="password" maxLength={512} autoComplete="new-password"
      placeholder={saved?.hasSigningKey ? "已保存，留空保留" : "填写信令服务器的签发密钥"}
      value={key} disabled={!saved||busy||removeKey} onChange={e=>setKey(e.target.value)}/></label>
    {saved?.hasSigningKey && <div className="fed-row"><label className="fed-row">
      <input type="checkbox" checked={removeKey} disabled={busy} onChange={e=>setRemoveKey(e.target.checked)}/>移除已保存的密钥</label></div>}
    <details className="fed-ice-options">
    <summary>其他 STUN / TURN 服务器（可选）</summary>
    {iceServers.map((row,index)=>{
      const turn = /^turns?:/i.test(row.url.trim());
      const savedPassword = savedIce.some(server=>server.url === row.url.trim() && server.username === row.username.trim() && server.hasCredential);
      return <fieldset className="fed-ice-server fed-fields" key={index} disabled={busy}>
        <legend>服务器 {index+1}</legend>
        <label>STUN / TURN 地址<input value={row.url} maxLength={512} spellCheck={false}
          placeholder="turn:relay.example.com:3478?transport=tcp" onChange={e=>editIce(index,"url",e.target.value)}/></label>
        {turn && <>
          <label>TURN 用户名<input value={row.username} maxLength={512} autoComplete="off"
            onChange={e=>editIce(index,"username",e.target.value)}/></label>
          <label>TURN 密码<input type="password" value={row.credential} maxLength={512} autoComplete="new-password"
            placeholder={savedPassword?"已保存，留空保留":undefined} onChange={e=>editIce(index,"credential",e.target.value)}/></label>
        </>}
        <button type="button" className="text-button" onClick={()=>setIceServers(rows=>rows.filter((_,i)=>i!==index))}>移除此服务器</button>
      </fieldset>;
    })}
    <button type="button" className="button" disabled={!saved||busy}
      onClick={()=>setIceServers(rows=>[...rows,{url:"",username:"",credential:""}])}>添加 STUN / TURN 服务器</button>
    </details>
    {saved?.readError && <small role="alert">无法解密已保存的配置，请重新填写。</small>}
    {saved?.bridgeBlockReason === "bridge_missing" && <small>缺少 WebRTC 后台组件。</small>}
    {saved?.bridgeBlockReason === "restart_required" && <small>已找到 WebRTC 后台组件，重启工作台后生效。</small>}
    <div className="fed-row">
      <button type="button" className="button" disabled={!saved||busy||!dirty} onClick={()=>perform(async()=>{
        apply(await request<Settings>(path,{method:"PUT",body:{origin,fingerprint,signingKey:removeKey?"":key.trim()||null,
          iceServers:iceServers.map(row=>({...row,credential:row.credential||null}))}}));
        setFeedback("已保存信令设置");
      })}>保存信令设置</button>
      <button type="button" className="button" disabled={!configured||dirty||busy} onClick={()=>perform(async()=>{
        const result = await request<{connected:boolean}>("/peers/signaling");
        setFeedback(result.connected ? "信令连接成功，公钥指纹已核对" : "无法连接信令服务器，请检查地址和公钥指纹");
      })}>测试信令连接</button>
    </div>
    <small role="status">{feedback}</small>
  </section>;
}
