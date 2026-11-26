import {useEffect, useState} from "react";
import {request} from "../../services/api";

interface LanAccess {address:string|null; client:string|null}
export function LanAccessSettings() {
  const [access,setAccess]=useState<LanAccess>(),[code,setCode]=useState(""),[error,setError]=useState("");
  useEffect(()=>{
    const dialog=document.querySelector<HTMLDialogElement>("#settings-dialog")!;
    let active=true;
    function load() {
      setCode("");setError("");
      if (dialog.open) request<LanAccess>("/lan-access").then(data=>{if(active)setAccess(data);})
        .catch(e=>{if(active)setError(e.message);});
    }
    dialog.addEventListener("toggle",load);
    return ()=>{active=false;dialog.removeEventListener("toggle",load);};
  },[]);
  async function pairing(revoke:boolean) {
    try {
      setError("");
      if (revoke) {
        await request("/lan-access/session",{method:"DELETE"});
        setCode("");setAccess(await request<LanAccess>("/lan-access"));
      } else {
        const data=await request<{code:string}>("/lan-access/pairing",{method:"POST"});
        setCode(data.code);
      }
    } catch(e){setError(e instanceof Error?e.message:"操作失败");}
  }
  return <section className="fed-fields"><h3>局域网开发</h3>
    {access?.address ? <>
      <small>在可信局域网的另一台电脑打开 <code>{access.address}</code>，输入配对码即可操作此工作台。</small>
      {access.client && <small>已配对浏览器 · {access.client}</small>}
      <div className="fed-row"><button className="button" type="button" onClick={()=>pairing(false)}>生成配对码</button>
        <button className="text-button" type="button" onClick={()=>pairing(true)}>撤销配对</button></div>
      {code && <label>一次性配对码（10 分钟有效）<input readOnly value={code} onFocus={e=>e.target.select()}/></label>}
    </> : access && <small>启动工作台时添加 <code>--lan-http-port 5187</code>，即可在可信局域网使用 HTTP 远程开发。</small>}
    {error && <small role="alert">{error}</small>}
  </section>;
}
