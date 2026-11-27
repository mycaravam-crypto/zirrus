import { MantineProvider } from "@mantine/core";
import { QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { createZirrusClient } from "@zirrus/api-client";
import { afterEach, expect, test, vi } from "vitest";
import { App } from "./App";
import { detectLanguage, setLanguage, t } from "./i18n";
import { createQueryClient } from "./queryClient";
import { theme } from "./theme";

afterEach(() => {
  vi.unstubAllGlobals();
  setLanguage("en");
});

const catalogId = "8c6f9b1e-0000-4000-8000-000000000001";
const assetId = "3f0e1d2c-0000-4000-8000-0000000000a1";
const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const me = (name = "Erin") => () => json(200, { subject: "u1", name, isOrganizationAdmin: false });
const catalogs = (access: string) => () => json(200, [{ id: catalogId, name: "Press photos", createdAt: "2026-11-26T12:00:00Z", access }]);
const csrf = () => json(200, { token: "t0k3n", formFieldName: "__RequestVerificationToken" });
const summary = { id: assetId, fileName: "harbour.jpg", mediaType: "image/jpeg", sizeBytes: 2048, state: "active", statusReason: null, createdAt: "2026-11-26T12:00:00Z", updatedAt: "2026-11-26T12:00:00Z" };
const details = (overrides: Record<string, unknown> = {}) => ({
  ...summary,
  catalogId,
  sha256: "ab".repeat(32),
  metadata: { title: "Harbour", rating: 5 },
  revision: 4,
  ...overrides,
});

type Route = (request: Request) => Response | Promise<Response>;

/** Endpoints every explorer view calls; tests override them as needed. */
const defaults: Record<string, Route> = {
  [`GET /api/v1/catalogs/${catalogId}/metadata-schema`]: () => json(200, { version: 0, fields: [] }),
  [`GET /api/v1/catalogs/${catalogId}/categories`]: () => json(200, []),
  [`GET /api/v1/catalogs/${catalogId}/assets/facets`]: () => json(200, { total: 0, categories: [] }),
  [`GET /api/v1/catalogs/${catalogId}/assets/${assetId}/categories`]: () => json(200, []),
};

/** Renders the app with fetch routed by "METHOD /path"; returns the recorded requests. */
function renderWith(overrides: Record<string, Route>) {
  const routes = { ...defaults, ...overrides };
  const requests: Request[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn((request: Request) => {
      requests.push(request);
      const url = new URL(request.url);
      const route = routes[`${request.method} ${url.pathname}`];
      return Promise.resolve(route ? route(request) : json(404, {}));
    }),
  );
  render(
    <MantineProvider theme={theme} env="test">
      <QueryClientProvider client={createQueryClient({ retry: false })}>
        <App client={createZirrusClient("http://localhost")} />
      </QueryClientProvider>
    </MantineProvider>,
  );
  return requests;
}

const assetsPath = `/api/v1/catalogs/${catalogId}/assets`;
const assetUrl = `${assetsPath}/${assetId}`;

test("anonymous visitors get a sign-in link", async () => {
  renderWith({ "GET /api/v1/me": () => json(401, {}) });
  expect(await screen.findByRole("link", { name: "Sign in" })).toHaveAttribute("href", "/login?returnUrl=/");
});

test("shows an error when the API fails", async () => {
  renderWith({ "GET /api/v1/me": () => json(500, {}) });
  expect(await screen.findByRole("alert")).toHaveTextContent("Could not reach the server.");
});

test("signed-in users see catalogs with access and a sign-out form", async () => {
  renderWith({ "GET /api/v1/me": me(), "GET /api/v1/catalogs": catalogs("editor"), "GET /api/v1/antiforgery": csrf });
  expect(await screen.findByRole("button", { name: /Press photos/ })).toHaveTextContent("editor") // access badge;
  const form = (await screen.findByRole("button", { name: "Sign out" })).closest("form");
  expect(form).toHaveAttribute("action", "/logout");
  expect(form?.querySelector('input[name="__RequestVerificationToken"]')).toHaveValue("t0k3n");
});

test("viewers browse and open assets but get no upload, retry or edit actions", async () => {
  renderWith({
    "GET /api/v1/me": me("Victor"),
    "GET /api/v1/catalogs": catalogs("viewer"),
    "GET /api/v1/antiforgery": csrf,
    [`GET ${assetsPath}`]: () => json(200, { items: [summary], nextCursor: null }),
    [`GET ${assetUrl}`]: () => json(200, details()),
  });
  fireEvent.click(await screen.findByRole("button", { name: /Press photos/ }));
  fireEvent.click(await screen.findByRole("button", { name: /^Open\s+harbour\.jpg/ }));

  const pane = await screen.findByRole("complementary", { name: "harbour.jpg" });
  expect(within(pane).getByRole("img", { name: "Preview of harbour.jpg" })).toHaveAttribute("src", `${assetUrl}/renditions/preview`);
  expect(within(pane).getByRole("link", { name: "Download original" })).toHaveAttribute("href", `${assetUrl}/content`);
  expect(await within(pane).findByText("You can view but not change this asset.")).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Save metadata" })).not.toBeInTheDocument();
  expect(screen.queryByLabelText(/Choose images/)).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Retry processing" })).not.toBeInTheDocument();
});

test("editors save metadata with If-Match and see a conflict on 412", async () => {
  let saves = 0;
  const requests = renderWith({
    "GET /api/v1/me": me(),
    "GET /api/v1/catalogs": catalogs("editor"),
    "GET /api/v1/antiforgery": csrf,
    [`GET ${assetsPath}`]: () => json(200, { items: [summary], nextCursor: null }),
    [`GET ${assetUrl}`]: () => json(200, details()),
    [`PUT ${assetUrl}/metadata`]: () => (++saves === 1 ? json(200, details({ metadata: { title: "Quay", rating: 5 }, revision: 5 })) : json(412, { title: "stale" })),
  });
  fireEvent.click(await screen.findByRole("button", { name: /Press photos/ }));
  fireEvent.click(await screen.findByRole("button", { name: /^Open\s+harbour\.jpg/ }));
  const value = await screen.findByDisplayValue("Harbour");
  expect(screen.getByText("(Structured value (edited in a later version))")).toBeInTheDocument();

  fireEvent.change(value, { target: { value: "Quay" } });
  fireEvent.click(screen.getByRole("button", { name: "Save metadata" }));
  expect(await screen.findByText("Metadata saved.")).toBeInTheDocument();
  const [put] = requests.filter((r) => r.method === "PUT");
  if (put === undefined) throw new Error("no PUT was sent");
  expect(put.headers.get("If-Match")).toBe('"4"');
  expect(put.headers.get("X-CSRF-TOKEN")).toBe("t0k3n");
  expect(await put.clone().json()).toEqual({ title: "Quay", rating: 5 }); // structured values kept

  fireEvent.click(await screen.findByRole("button", { name: "Save metadata" }));
  expect(await screen.findByText(/Someone else changed this asset/)).toBeInTheDocument();
  const resent = requests.filter((r) => r.method === "PUT")[1];
  expect(resent?.headers.get("If-Match")).toBe('"5"');
  expect(screen.getByRole("button", { name: "Load the latest version" })).toBeInTheDocument();
});

test("invalid field names block saving", async () => {
  renderWith({
    "GET /api/v1/me": me(),
    "GET /api/v1/catalogs": catalogs("admin"),
    "GET /api/v1/antiforgery": csrf,
    [`GET ${assetsPath}`]: () => json(200, { items: [summary], nextCursor: null }),
    [`GET ${assetUrl}`]: () => json(200, details({ metadata: {} })),
  });
  fireEvent.click(await screen.findByRole("button", { name: /Press photos/ }));
  fireEvent.click(await screen.findByRole("button", { name: /^Open\s+harbour\.jpg/ }));
  fireEvent.click(await screen.findByRole("button", { name: "Add field" }));
  fireEvent.change(screen.getByLabelText("Field"), { target: { value: "Bad Key" } });
  expect(await screen.findByText(/Field names start with a lowercase letter/)).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Save metadata" })).toBeDisabled();
});

test("failed assets show their reason and editors can retry", async () => {
  const requests = renderWith({
    "GET /api/v1/me": me(),
    "GET /api/v1/catalogs": catalogs("editor"),
    "GET /api/v1/antiforgery": csrf,
    [`GET ${assetsPath}`]: () => json(200, { items: [{ ...summary, state: "failed", statusReason: "process: timeout" }], nextCursor: null }),
    [`GET ${assetUrl}`]: () => json(200, details({ state: "failed", statusReason: "process: timeout" })),
    [`POST ${assetUrl}/retry`]: () => json(200, details({ state: "processing", statusReason: null, revision: 5 })),
  });
  fireEvent.click(await screen.findByRole("button", { name: /Press photos/ }));
  expect(await screen.findByText("Failed")).toBeInTheDocument(); // grid badge
  fireEvent.click(screen.getByRole("button", { name: /^Open\s+harbour\.jpg/ }));
  expect(await screen.findByText("process: timeout")).toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: "Retry processing" }));
  await waitFor(() => expect(requests.some((r) => r.method === "POST" && r.url.endsWith("/retry"))).toBe(true));
  expect(await screen.findByText("Processing")).toBeInTheDocument();
});

test("a 401 during browsing returns to the sign-in link without showing cached data", async () => {
  let signedIn = true;
  renderWith({
    "GET /api/v1/me": () => (signedIn ? me()() : json(401, {})),
    "GET /api/v1/catalogs": catalogs("viewer"),
    "GET /api/v1/antiforgery": csrf,
    [`GET ${assetsPath}`]: () => {
      signedIn = false;
      return json(401, {});
    },
  });
  fireEvent.click(await screen.findByRole("button", { name: /Press photos/ }));
  expect(await screen.findByRole("link", { name: "Sign in" })).toBeInTheDocument();
  expect(screen.queryByText(/Press photos/)).not.toBeInTheDocument();
});

test("uploads report progress, duplicates and refresh the grid", async () => {
  const sent: XMLHttpRequestBodyInit[] = [];
  class FakeXhr {
    status = 0;
    response: unknown = null;
    responseType = "";
    headers: Record<string, string> = {};
    upload: { onprogress: ((e: { lengthComputable: boolean; loaded: number; total: number }) => void) | null } = { onprogress: null };
    onload: (() => void) | null = null;
    onerror: (() => void) | null = null;
    open() {}
    setRequestHeader(name: string, value: string) {
      this.headers[name] = value;
    }
    send(body: XMLHttpRequestBodyInit) {
      sent.push(body);
      expect(this.headers["X-CSRF-TOKEN"]).toBe("t0k3n");
      this.upload.onprogress?.({ lengthComputable: true, loaded: 50, total: 100 });
      this.status = 201;
      this.response = { asset: details({ state: "quarantined" }), duplicates: ["d1"] };
      this.onload?.();
    }
  }
  vi.stubGlobal("XMLHttpRequest", FakeXhr);
  let listed = 0;
  renderWith({
    "GET /api/v1/me": me(),
    "GET /api/v1/catalogs": catalogs("editor"),
    "GET /api/v1/antiforgery": csrf,
    [`GET ${assetsPath}`]: () => (listed++ === 0 ? json(200, { items: [], nextCursor: null }) : json(200, { items: [{ ...summary, state: "quarantined" }], nextCursor: null })),
  });
  fireEvent.click(await screen.findByRole("button", { name: /Press photos/ }));
  expect(await screen.findByText("No assets yet.")).toBeInTheDocument();

  const file = new File([new Uint8Array([0xff, 0xd8, 0xff])], "harbour.jpg", { type: "image/jpeg" });
  fireEvent.change(screen.getByLabelText(/Choose images/), { target: { files: [file] } });
  expect(await screen.findByText(/harbour.jpg uploaded; scanning and processing start now. harbour.jpg matches 1 existing/)).toBeInTheDocument();
  expect((sent[0] as FormData).get("file")).toBeInstanceOf(File);
  expect(await screen.findByText("Waiting for scan")).toBeInTheDocument();
});

test("a 401 while saving returns to the sign-in link", async () => {
  let signedIn = true;
  renderWith({
    "GET /api/v1/me": () => (signedIn ? me()() : json(401, {})),
    "GET /api/v1/catalogs": catalogs("editor"),
    "GET /api/v1/antiforgery": csrf,
    [`GET ${assetsPath}`]: () => json(200, { items: [summary], nextCursor: null }),
    [`GET ${assetUrl}`]: () => json(200, details()),
    [`PUT ${assetUrl}/metadata`]: () => {
      signedIn = false;
      return json(401, {});
    },
  });
  fireEvent.click(await screen.findByRole("button", { name: /Press photos/ }));
  fireEvent.click(await screen.findByRole("button", { name: /^Open\s+harbour\.jpg/ }));
  fireEvent.click(await screen.findByRole("button", { name: "Save metadata" }));
  expect(await screen.findByRole("link", { name: "Sign in" })).toBeInTheDocument();
  expect(screen.queryByText(/Press photos/)).not.toBeInTheDocument();
});

test("with a schema, the editor shows typed fields, validates them and saves typed values", async () => {
  const schema = {
    version: 1,
    fields: [
      { key: "title", label: "Title", type: 0, required: true, multiple: false },
      { key: "rating", label: "Rating", type: 2, required: false, multiple: false },
      { key: "published", label: "Published", type: 4, required: false, multiple: false },
      { key: "captured", label: "Captured", type: 3, required: false, multiple: false },
    ],
  };
  const requests = renderWith({
    "GET /api/v1/me": me(),
    "GET /api/v1/catalogs": catalogs("editor"),
    "GET /api/v1/antiforgery": csrf,
    [`GET /api/v1/catalogs/${catalogId}/metadata-schema`]: () => json(200, schema),
    [`GET ${assetsPath}`]: () => json(200, { items: [summary], nextCursor: null }),
    [`GET ${assetUrl}`]: () => json(200, details({ metadata: { title: "Harbour", rating: 5, legacy: "x" } })),
    [`PUT ${assetUrl}/metadata`]: () => json(200, details({ metadata: { title: "Quay", rating: 4.5 }, revision: 5 })),
  });
  fireEvent.click(await screen.findByRole("button", { name: /Press photos/ }));
  fireEvent.click(await screen.findByRole("button", { name: /^Open\s+harbour\.jpg/ }));

  const title = await screen.findByRole("textbox", { name: /Title/ });
  expect(title).toHaveValue("Harbour");
  expect(screen.getByRole("textbox", { name: "Rating" })).toHaveValue("5");
  expect(screen.getByText(/will be removed when you save: legacy/)).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Add field" })).not.toBeInTheDocument();

  fireEvent.change(title, { target: { value: " " } });
  fireEvent.change(screen.getByRole("textbox", { name: "Rating" }), { target: { value: "lots" } });
  fireEvent.change(screen.getByRole("textbox", { name: /Captured/ }), { target: { value: "2026-11-27T10:00:00" } });
  fireEvent.click(screen.getByRole("button", { name: "Save metadata" }));
  expect(await screen.findByText("Title is required.")).toBeInTheDocument();
  expect(screen.getByText("Rating: enter a number.")).toBeInTheDocument();
  expect(screen.getByText(/Captured: enter a date/)).toBeInTheDocument();
  expect(requests.some((r) => r.method === "PUT")).toBe(false);

  fireEvent.change(title, { target: { value: "Quay" } });
  fireEvent.change(screen.getByRole("textbox", { name: "Rating" }), { target: { value: "4.5" } });
  fireEvent.change(screen.getByRole("textbox", { name: /Captured/ }), { target: { value: "2026-11-27" } });
  fireEvent.click(screen.getByRole("button", { name: "Save metadata" }));
  expect(await screen.findByText("Metadata saved.")).toBeInTheDocument();
  const put = requests.find((r) => r.method === "PUT");
  expect(await put?.clone().json()).toEqual({ title: "Quay", rating: 4.5, captured: "2026-11-27" }); // legacy key dropped
});

test("categories filter the grid with counts, and editors assign and remove them", async () => {
  const harbour = { id: "c0000000-0000-4000-8000-000000000001", parentId: null, name: "Harbour" };
  let assigned: (typeof harbour)[] = [];
  const requests = renderWith({
    "GET /api/v1/me": me(),
    "GET /api/v1/catalogs": catalogs("editor"),
    "GET /api/v1/antiforgery": csrf,
    [`GET /api/v1/catalogs/${catalogId}/categories`]: () => json(200, [harbour]),
    [`GET /api/v1/catalogs/${catalogId}/assets/facets`]: () => json(200, { total: 3, categories: [{ categoryId: harbour.id, name: "Harbour", count: 2 }] }),
    [`GET ${assetsPath}`]: () => json(200, { items: [summary], nextCursor: null }),
    [`GET ${assetUrl}`]: () => json(200, details()),
    [`GET ${assetUrl}/categories`]: () => json(200, assigned),
    [`PUT ${assetUrl}/categories/${harbour.id}`]: () => {
      assigned = [harbour];
      return json(200, harbour);
    },
    [`DELETE ${assetUrl}/categories/${harbour.id}`]: () => {
      assigned = [];
      return json(200, []);
    },
  });
  fireEvent.click(await screen.findByRole("button", { name: /Press photos/ }));
  const category = await screen.findByRole("button", { name: /Harbour/ });
  expect(await within(category).findByText("2 matching")).toBeInTheDocument();
  fireEvent.click(category);
  await waitFor(() => expect(requests.some((r) => r.url.includes(`categoryId=${harbour.id}`))).toBe(true));
  expect(screen.getByText("Showing assets in Harbour")).toBeInTheDocument();

  fireEvent.click(await screen.findByRole("button", { name: /^Open\s+harbour\.jpg/ }));
  expect(await screen.findByText("Not in any category.")).toBeInTheDocument();
  fireEvent.click(screen.getByLabelText("Add to category", { selector: "input" }));
  fireEvent.click(await screen.findByRole("option", { name: "Harbour" }));
  const remove = await screen.findByRole("button", { name: "Remove from Harbour" });
  expect(requests.find((r) => r.method === "PUT" && r.url.endsWith(`/categories/${harbour.id}`))?.headers.get("X-CSRF-TOKEN")).toBe("t0k3n");
  fireEvent.click(remove);
  expect(await screen.findByText("Not in any category.")).toBeInTheDocument();
});

test("German browsers get German strings with placeholders filled", () => {
  expect(detectLanguage(["de-CH", "en"])).toBe("de");
  expect(detectLanguage(["fr-FR"])).toBe("en");
  setLanguage("de");
  expect(t("app.signIn")).toBe("Anmelden");
  expect(t("upload.progress", { name: "a.jpg", percent: 40 })).toBe("a.jpg wird hochgeladen: 40 %");
});
