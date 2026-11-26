import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { createZirrusClient } from "@zirrus/api-client";
import { afterEach, expect, test, vi } from "vitest";
import { App } from "./App";

afterEach(() => vi.unstubAllGlobals());

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

function renderWith(routes: Record<string, () => Response>) {
  vi.stubGlobal(
    "fetch",
    vi.fn((request: Request) => {
      const path = new URL(request.url).pathname;
      const route = routes[path];
      return Promise.resolve(route ? route() : json(404, {}));
    }),
  );
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <App client={createZirrusClient("http://localhost")} />
    </QueryClientProvider>,
  );
}

test("anonymous visitors get a sign-in link", async () => {
  renderWith({ "/api/v1/me": () => json(401, {}) });
  expect(await screen.findByRole("link", { name: "Sign in" })).toHaveAttribute("href", "/login?returnUrl=/");
});

test("signed-in users see their catalogs with access and a sign-out form", async () => {
  renderWith({
    "/api/v1/me": () => json(200, { subject: "u1", name: "Erin", isOrganizationAdmin: false }),
    "/api/v1/catalogs": () =>
      json(200, [{ id: "8c6f9b1e-0000-4000-8000-000000000001", name: "Press photos", createdAt: "2026-11-26T12:00:00Z", access: "editor" }]),
    "/api/v1/antiforgery": () => json(200, { token: "t0k3n", formFieldName: "__RequestVerificationToken" }),
  });
  expect(await screen.findByText("Press photos")).toBeInTheDocument();
  expect(screen.getByText("(editor)")).toBeInTheDocument();
  const button = await screen.findByRole("button", { name: "Sign out" });
  const form = button.closest("form");
  expect(form).toHaveAttribute("action", "/logout");
  expect(form?.querySelector('input[name="__RequestVerificationToken"]')).toHaveValue("t0k3n");
});

test("shows an error when the API fails", async () => {
  renderWith({ "/api/v1/me": () => json(500, {}) });
  expect(await screen.findByRole("alert")).toHaveTextContent("Could not reach the server.");
});
