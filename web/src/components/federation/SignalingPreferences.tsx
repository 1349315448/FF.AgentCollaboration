import {useEffect, useState} from "react";
import {request} from "../../services/api";

interface Settings {
  origin:string|null;
  fingerprint:string|null;
  hasSigningKey:boolean;
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
  const [busy,setBusy] = useState(false);
  const [feedback,setFeedback] = useState("");

  function apply(settings:Settings) {
    setSaved(settings);
    setOrigin(settings.origin ?? "");
    setFingerprint(settings.fingerprint ?? "");
    setKey("");
    setRemoveKey(false);
  }

  useEffect(()=>{
    const dialog = document.querySelector<HTMLDialogElement>("#settings-dialog")!;
    let load:AbortController|undefined;
    function toggled() {
      load?.abort();
      setKey("");
      setRemoveKey(false);
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
  const dirty = origin.trim() !== (saved?.origin ?? "") || fingerprint.trim() !== (saved?.fingerprint ?? "") || Boolean(key) || removeKey;
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
    {saved?.readError && <small role="alert">无法解密已保存的配置，请重新填写。</small>}
    {saved?.bridgeBlockReason === "bridge_missing" && <small>缺少 WebRTC 后台组件。</small>}
    {saved?.bridgeBlockReason === "restart_required" && <small>已找到 WebRTC 后台组件，重启工作台后生效。</small>}
    <div className="fed-row">
      <button type="button" className="button" disabled={!saved||busy||!dirty} onClick={()=>perform(async()=>{
        apply(await request<Settings>(path,{method:"PUT",body:{origin,fingerprint,signingKey:removeKey?"":key.trim()||null}}));
        setFeedback("已保存信令设置");
      })}>保存信令设置</button>
      <button type="button" className="button" disabled={!configured||dirty||busy} onClick={()=>perform(async()=>{
        const result = await request<{connected:boolean}>("/peers/signaling");
        setFeedback(result.connected ? "连接成功，公钥指纹已核对" : "无法连接，请检查服务地址和公钥指纹");
      })}>测试连接</button>
    </div>
    <small role="status">{feedback}</small>
  </section>;
}
