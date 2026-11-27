import { QueryClientProvider } from "@tanstack/react-query";
import { createZirrusClient } from "@zirrus/api-client";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App";
import { detectLanguage, setLanguage } from "./i18n";
import { createQueryClient } from "./queryClient";
import "./styles.css";

const root = document.getElementById("root");
if (!root) throw new Error("missing #root element");

const language = detectLanguage();
setLanguage(language);
document.documentElement.lang = language;

createRoot(root).render(
  <StrictMode>
    <QueryClientProvider client={createQueryClient()}>
      <App client={createZirrusClient()} />
    </QueryClientProvider>
  </StrictMode>,
);
