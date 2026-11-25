export function MentionPickerPopover() {
  return (
    <dialog id="agents-dialog" className="mention-picker" aria-label="指定在线 Agent" closedby="any">
      <div className="peer-search-box">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" aria-hidden="true">
          <circle cx="10.5" cy="10.5" r="6.5" /><path d="m16 16 5 5" />
        </svg>
        <input type="search" id="peer-search" role="combobox" aria-label="搜索在线 Agent" aria-controls="agent-options"
          aria-expanded="true" aria-autoComplete="list" placeholder="搜索 Agent、会话或话题" autoComplete="off" ref={input => { if (input) input.autofocus = true; }} />
      </div>
      <div id="agent-options" role="listbox" aria-label="在线 Agent"></div>
      <small id="peer-status" role="status"></small>
    </dialog>
  );
}
