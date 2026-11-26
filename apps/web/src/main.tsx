import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { createZirrusClient } from "@zirrus/api-client";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App";

const root = document.getElementById("root");
if (!root) throw new Error("missing #root element");

createRoot(root).render(
  <StrictMode>
    <QueryClientProvider client={new QueryClient()}>
      <App client={createZirrusClient()} />
    </QueryClientProvider>
  </StrictMode>,
);
