import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter } from "react-router";
import { App } from "./App";
import { SessionProvider } from "./auth/session";
import { I18nProvider } from "./i18n";
import "./styles.css";
import "./remote.css";

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <I18nProvider>
      <BrowserRouter>
        <SessionProvider>
          <App />
        </SessionProvider>
      </BrowserRouter>
    </I18nProvider>
  </StrictMode>,
);
