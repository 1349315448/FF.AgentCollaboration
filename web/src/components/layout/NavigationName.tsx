import { useEffect, useRef, useState } from "react";

interface Props {
  className: string;
  children: string;
  title: string;
  current?: boolean;
  onSelect?: () => void;
  onRename: () => void;
}

export function NavigationName({className, children, title, current, onSelect, onRename}: Props) {
  const pending = useRef<number | undefined>(undefined);
  const [choosing, setChoosing] = useState(false);
  const cancelSelection = () => { window.clearTimeout(pending.current); setChoosing(false); };
  useEffect(() => () => window.clearTimeout(pending.current), []);

  return <button type="button" className={className} data-choosing={choosing || undefined} aria-current={current ? "page" : undefined}
    title={title + " · 双击或按 F2 改名"} aria-keyshortcuts="F2"
    onClick={event => {
      cancelSelection();
      if (event.detail === 0) (onSelect ?? onRename)();
      // 单击可能收起窗格或关闭窄屏侧栏，先给双击改名留出时间。
      else if (event.detail === 1 && onSelect) {
        setChoosing(true);
        pending.current = window.setTimeout(() => { setChoosing(false); onSelect(); }, 400);
      }
    }}
    onBlur={cancelSelection}
    onDoubleClick={() => { cancelSelection(); onRename(); }}
    onKeyDown={event => {
      if (event.key === "F2") {
        event.preventDefault();
        cancelSelection();
        onRename();
      }
    }}>{children}</button>;
}
