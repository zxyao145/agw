import { connect, type ExtensionApi } from "@agw/extension-sdk";
import { createRoot } from "react-dom/client";
import { FeishuSettings } from "./settings";
import "./styles.css";

const rootElement = document.getElementById("root");
if (!rootElement) throw new Error("The Feishu settings root is missing.");

const root = createRoot(rootElement);
root.render(<p className="loading">Connecting to Agw…</p>);

void connect().then((api: ExtensionApi) => root.render(<FeishuSettings api={api} />));
