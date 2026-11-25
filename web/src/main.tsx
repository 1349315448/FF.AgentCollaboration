import { createRoot } from "react-dom/client";
import { flushSync } from "react-dom";
import { App } from "./App";
import "./styles/workbench.css";

flushSync(() => createRoot(document.getElementById("root")!).render(<App />));
void import("./runtime/start");
