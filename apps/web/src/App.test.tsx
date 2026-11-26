import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { createZirrusClient } from "@zirrus/api-client";
import { afterEach, expect, test, vi } from "vitest";
import { App } from "./App";

afterEach(() => vi.unstubAllGlobals());

function renderWith(response: Response) {
  vi.stubGlobal("fetch", vi.fn().mockResolvedValue(response));
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <App client={createZirrusClient("http://localhost")} />
    </QueryClientProvider>,
  );
}

test("lists catalogs returned by the API", async () => {
  renderWith(
    new Response(JSON.stringify([{ id: "8c6f9b1e-0000-4000-8000-000000000001", name: "Press photos", createdAt: "2026-11-26T12:00:00Z" }]), {
      status: 200,
      headers: { "Content-Type": "application/json" },
    }),
  );
  expect(await screen.findByText("Press photos")).toBeInTheDocument();
});

test("shows an error when the API fails", async () => {
  renderWith(new Response("{}", { status: 500, headers: { "Content-Type": "application/json" } }));
  expect(await screen.findByRole("alert")).toHaveTextContent("Could not load catalogs.");
});
