import { flyMessage, messageAnchor } from "./activity.js";
import { byId, feedback, folderName } from "./dom.js";
import { folderListing } from "./folders.js";
import { layoutKey, renderLayout, setLayoutProbe } from "./layout.js";
import { openPeers } from "./mentions.js";
import { projectKey } from "../services/navigation";
import { bytes, canIgnore, updatePane } from "./pane.js";
import { fitProjectPath } from "../services/projectPath";
import { panes } from "../services/terminalRegistry";
import { checkSetup } from "./setup.js";
import { layout, mentionShortcut, selectedTopicId, snapshot } from "../store/workbenchStore";
import { fitPane, observePrompt, readNativePrompt, replayReports, reportPrompt } from "./terminal.js";
import { projectTab, renderTopics, selectedTopic } from "./topics.js";
import { refresh } from "./workbench.js";
import "./settings.js";

const probes = {
  get bytes() { return bytes; },
  get feedback() { return feedback; },
  get canIgnore() { return canIgnore; },
  get byId() { return byId; },
  get panes() { return panes; },
  get snapshot() { return snapshot; },
  get selectedTopic() { return selectedTopic; },
  get selectedTopicId() { return selectedTopicId; },
  get layout() { return layout; },
  get folderListing() { return folderListing; },
  get folderName() { return folderName; },
  get layoutKey() { return layoutKey; },
  get fitProjectPath() { return fitProjectPath; },
  get projectKey() { return projectKey; },
  get projectTab() { return projectTab; },
  get renderTopics() { return renderTopics; },
  get refresh() { return refresh; },
  get renderLayout() { return renderLayout; },
  get updatePane() { return updatePane; },
  get readNativePrompt() { return readNativePrompt; },
  get reportPrompt() { return reportPrompt; },
  get observePrompt() { return observePrompt; },
  get fitPane() { return fitPane; },
  get replayReports() { return replayReports; },
  get messageAnchor() { return messageAnchor; },
  get flyMessage() { return flyMessage; },
  get openPeers() { return openPeers; },
  get mentionShortcut() { return mentionShortcut; },
  set renderLayout(value) { setLayoutProbe(value); }
};
Object.defineProperties(window, Object.getOwnPropertyDescriptors(probes));
checkSetup();
