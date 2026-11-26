export async function copyText(text: string) {
  if (navigator.clipboard) {
    try { await navigator.clipboard.writeText(text); return; } catch { /* Try the selected text on HTTP or when clipboard access is denied. */ }
  }
  const previous = document.activeElement instanceof HTMLElement ? document.activeElement : null;
  const input = document.createElement("textarea");
  input.value = text;
  input.readOnly = true;
  input.style.cssText = "position:fixed;left:0;top:0;opacity:0";
  (previous?.closest("dialog[open]") ?? document.body).append(input);
  try {
    input.select();
    if (!document.execCommand("copy")) throw new Error("浏览器未允许复制，请手动复制");
  } finally {
    input.remove();
    previous?.focus({preventScroll:true});
  }
}
