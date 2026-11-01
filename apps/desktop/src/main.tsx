import React from "react";
import ReactDOM from "react-dom/client";
import App from "../../portal/src/App";
import { readDesktopSession, isDesktopApp } from "../../portal/src/desktop-bridge";
import { storeTokens } from "../../portal/src/api";
import "../../portal/src/styles.css";
import "./desktop.css";

async function start() {
  document.body.dataset.appShell = "desktop";
  if (!isDesktopApp()) {
    document.body.innerHTML = "<main style='font:16px sans-serif;padding:32px'>SmartPrint Desktop must run inside its Windows application.</main>";
    return;
  }

  try {
    const saved = await readDesktopSession();
    if (saved) {
      const parsed = JSON.parse(saved);
      if (parsed && typeof parsed.accessToken === "string" && typeof parsed.refreshToken === "string") {
        await storeTokens(parsed);
      }
    }
  } catch {
    // A missing or invalid secure session simply returns the owner to sign-in.
    await storeTokens(null);
  }

  if (window.location.pathname !== "/shop") {
    window.history.replaceState(null, "", "/shop");
  }

  ReactDOM.createRoot(document.getElementById("root")!).render(
    <React.StrictMode><App /></React.StrictMode>
  );
}

void start();