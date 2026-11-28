const markedEnvelope = "M2 7h12v9H2z M2.5 7.5l5.5 4 5.5-4 ";
const paths = {
  mailRequest: markedEnvelope + "M16 1v5m-2.5-2.5h5",
  mailIncoming: markedEnvelope + "M18 1l-5 5m0-4v4h4",
  mailOutgoing: markedEnvelope + "M13 6l5-5m-4 0h4v4",
  mailReply: markedEnvelope + "M18 6V4h-6m2.5-2.5L12 4l2.5 2.5",
  mailNotice: markedEnvelope + "M17 2v3m0 2v.25",
  envelope: "M2.5 4.5h15v11h-15z M3 5l7 5 7-5",
  mailbox: "M2 14V9a4 4 0 0 1 4-4h7a4 4 0 0 1 4 4v5H2z M6 5a4 4 0 0 1 4 4v5 M13 10V3h4",
  network: "M8 12l4-4 M6.8 8.4 5.1 10a3 3 0 0 0 4.3 4.3l2-2 M13.2 11.6l1.7-1.6a3 3 0 0 0-4.3-4.3l-2 2",
  flag: "M4 17V3m0 2c4-3 8 3 12 0v8c-4 3-8-3-12 0",
  terminal: "M3 4h14v12H3z M6 8l2 2-2 2m4 0h3",
  code: "M7 6l-4 4 4 4m6-8 4 4-4 4m-2-11-2 14",
  panel: "M2.5 4h15v12h-15z M11.5 4v12",
  rail: "M2.5 4h15v12h-15z M8.5 4v12",
  changes: "M6 4v9m1-4h3a4 4 0 0 0 4-4 M6 2a2 2 0 1 0 .01 0 M6 13a2 2 0 1 0 .01 0 M14 2a2 2 0 1 0 .01 0",
  files: "M2.5 5h5l2 2h8v9h-15z",
  docs: "M5 2.5h7l3 3V17H5z M8 9h4m-4 3h4",
  down: "m6 8 4 4 4-4", right: "m8 6 4 4-4 4", close: "m6 6 8 8 M14 6l-8 8",
  search: "M13 13l4 4 M14 8.5a5.5 5.5 0 1 1-11 0 5.5 5.5 0 0 1 11 0",
  refresh: "M16 8a6 6 0 1 0 0 5 M16 3v5h-5", copy:"M7 7h9v10H7z M4 13H3V3h9v1",
  settings: "M8.7 2h2.6l.6 2.1 1.5.9 2.2-.5 1.3 2.2-1.6 1.6v1.8l1.6 1.6-1.3 2.2-2.2-.5-1.5.9-.6 2.1H8.7l-.6-2.1-1.5-.9-2.2.5-1.3-2.2 1.6-1.6V8.3L3.1 6.7l1.3-2.2 2.2.5 1.5-.9z M12.1 9.3a2.1 2.1 0 1 0-4.2 0 2.1 2.1 0 1 0 4.2 0",
  send:"m3 9 14-6-5 14-2-6z M10 11l7-8", edit:"m4 12 8-8 4 4-8 8-5 1z M10 6l4 4"
};
export function WorkspaceIcon({name}: {name: keyof typeof paths}) {
  return <svg width="16" height="16" viewBox="0 0 20 20" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d={paths[name]} /></svg>;
}
