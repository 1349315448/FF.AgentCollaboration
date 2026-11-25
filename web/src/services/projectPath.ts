const projectMeasure = document.createElement("canvas").getContext("2d")!;
export function fitProjectPath(name: HTMLElement) {
  const path = name.dataset.path!, width = name.clientWidth;
  projectMeasure.font = getComputedStyle(name).font;
  if (!width || projectMeasure.measureText(path).width <= width) { name.textContent = path; return; }
  const chars = Array.from(path);
  function clipped(keep: number) {
    const head = Math.max(1, Math.floor(keep / 3));
    return chars.slice(0, head).join("") + "...." + chars.slice(chars.length - keep + head).join("");
  }
  let low = 2, high = chars.length - 1;
  while (low < high) {
    const middle = Math.ceil((low + high) / 2);
    if (projectMeasure.measureText(clipped(middle)).width <= width) low = middle;
    else high = middle - 1;
  }
  name.textContent = clipped(low);
}
