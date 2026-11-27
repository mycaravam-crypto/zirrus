import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import path from "node:path";
import { fileURLToPath } from "node:url";

// W6 critical journeys (#26) through Caddy, the API, Keycloak, the worker, the renderer and ClamAV.
// Users and passwords are the test-only ones from deploy/keycloak/zirrus-realm.json.
const users = { alice: "ci-test-only-alice", erin: "ci-test-only-erin" } as const;
const fixture = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../../tools/ci/fixtures/gradient-640x480.jpg");
const catalogName = `E2E UI ${Date.now()}`;

async function signIn(page: Page, user: keyof typeof users) {
  await page.goto("/");
  await page.getByRole("link", { name: "Sign in" }).click();
  await page.locator("#username").fill(user);
  await page.locator("#password").fill(users[user]);
  await page.locator("#kc-login").click();
  await expect(page.getByText(/^Signed in as/)).toBeVisible();
}

/** Zero critical or serious axe-core findings (Q6). */
async function expectAccessible(page: Page, label: string) {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  const blocking = results.violations.filter((v) => v.impact === "critical" || v.impact === "serious");
  expect(blocking.map((v) => `${v.id}: ${v.help} (${v.nodes.length})`), `axe findings on ${label}`).toEqual([]);
}

async function api(page: Page, method: "POST" | "PUT", url: string, data: unknown) {
  const token = (await (await page.request.get("/api/v1/antiforgery")).json()) as { token: string };
  const response = await page.request.fetch(url, { method, data, headers: { "X-CSRF-TOKEN": token.token } });
  expect(response.ok(), `${method} ${url}: ${response.status()}`).toBeTruthy();
  return response.json() as Promise<Record<string, unknown>>;
}

test.describe.configure({ mode: "serial" });

let catalogId = "";

test("editor journey: upload, processing, detail, metadata, download, logout", async ({ page }) => {
  await signIn(page, "alice"); // administrators act as editors in every catalog
  await expectAccessible(page, "explorer without a catalog");

  const catalog = await api(page, "POST", "/api/v1/catalogs", { name: catalogName });
  catalogId = String(catalog.id);
  await page.reload();

  await page.getByRole("button", { name: new RegExp(catalogName) }).click();
  await expect(page.getByRole("heading", { name: `Assets in ${catalogName}` })).toBeVisible();
  await page.getByLabel(/Choose images/).setInputFiles(fixture);
  await expect(page.getByText(/gradient-640x480.jpg uploaded/)).toBeVisible();

  // Wait for scan and processing: the card shows its thumbnail once the asset is active.
  const card = page.getByRole("button", { name: /^Open\s+gradient-640x480\.jpg/ });
  await expect(card.locator("img")).toBeVisible({ timeout: 8 * 60_000 });
  await expectAccessible(page, "asset grid");

  await card.click();
  const pane = page.getByRole("complementary", { name: "gradient-640x480.jpg" });
  await expect(pane.getByRole("img", { name: "Preview of gradient-640x480.jpg" })).toBeVisible();
  await expect(pane.getByText("Active", { exact: true })).toBeVisible();
  await expect(pane.getByRole("heading", { name: "gradient-640x480.jpg" })).toBeFocused();

  await pane.getByRole("button", { name: "Add field" }).click();
  await pane.getByRole("textbox", { name: "Field", exact: true }).fill("title");
  await pane.getByRole("textbox", { name: "Value", exact: true }).fill("Harbour at dusk");
  await pane.getByRole("button", { name: "Save metadata" }).click();
  await expect(pane.getByText("Metadata saved.")).toBeVisible();
  await expectAccessible(page, "asset detail with metadata form");

  // Categories: the administrator creates one in the navigation and assigns the asset to it.
  const categoriesNav = page.getByRole("navigation", { name: "Categories" });
  await page.getByRole("button", { name: "New category" }).click();
  await page.getByRole("textbox", { name: "Category name" }).fill("Harbour views");
  await page.getByRole("button", { name: "Create", exact: true }).click();
  await expect(page.getByText("Category Harbour views created.")).toBeVisible();
  await pane.getByRole("combobox", { name: "Add to category" }).click(); // the label also names the listbox
  await pane.getByRole("option", { name: "Harbour views" }).click();
  await expect(pane.getByRole("button", { name: "Remove from Harbour views" })).toBeVisible();
  await expectAccessible(page, "asset detail with categories");

  // Search: the saved title is found (stemmed), an unrelated term is not.
  const searchBox = page.getByRole("searchbox", { name: "Search assets" });
  await searchBox.fill("harbours");
  await page.getByRole("button", { name: "Search", exact: true }).click();
  await expect(card).toBeVisible();
  await searchBox.fill("zebra crossing");
  await page.getByRole("button", { name: "Search", exact: true }).click();
  await expect(page.getByText("No matching assets.")).toBeVisible();
  await searchBox.fill("");
  await page.getByRole("button", { name: "Search", exact: true }).click();

  // Filtering by the category shows the assigned asset and its count.
  await categoriesNav.getByRole("button", { name: /^Harbour views/ }).click();
  await expect(page.getByText("Showing assets in Harbour views")).toBeVisible();
  await expect(card).toBeVisible();
  await expectAccessible(page, "grid filtered by category");
  await page.getByRole("button", { name: "Show all assets" }).click();

  const [download] = await Promise.all([page.waitForEvent("download"), pane.getByRole("link", { name: "Download original" }).click()]);
  expect(download.suggestedFilename()).toBe("gradient-640x480.jpg");

  await page.getByRole("button", { name: "Sign out" }).click();
  await expect(page.getByRole("link", { name: "Sign in" })).toBeVisible();
});

test("viewer journey: read-only explorer, and the API refuses edits too (Q3)", async ({ browser }) => {
  // The administrator grants erin the viewer role in the catalog from the first test.
  const adminContext = await browser.newContext();
  const admin = await adminContext.newPage();
  await signIn(admin, "alice");
  const erinPage = await (await browser.newContext()).newPage();
  await signIn(erinPage, "erin");
  const erin = (await (await erinPage.request.get("/api/v1/me")).json()) as { subject: string };
  await api(admin, "PUT", `/api/v1/catalogs/${catalogId}/members/${erin.subject}`, { role: "viewer" });
  await adminContext.close();

  await erinPage.reload();
  await erinPage.getByRole("button", { name: new RegExp(catalogName) }).click();
  await expect(erinPage.getByLabel(/Choose images/)).toHaveCount(0);
  await erinPage.getByRole("button", { name: /^Open\s+gradient-640x480\.jpg/ }).click();
  const pane = erinPage.getByRole("complementary", { name: "gradient-640x480.jpg" });
  await expect(pane.getByText("You can view but not change this asset.")).toBeVisible();
  await expect(pane.getByText("Harbour at dusk")).toBeVisible();
  await expect(pane.getByRole("button", { name: "Save metadata" })).toHaveCount(0);
  await expectAccessible(erinPage, "viewer detail");

  // Bypassing the UI does not help: the API answers 403 for a viewer's metadata edit.
  const list = (await (await erinPage.request.get(`/api/v1/catalogs/${catalogId}/assets`)).json()) as { items: { id: string }[] };
  const token = (await (await erinPage.request.get("/api/v1/antiforgery")).json()) as { token: string };
  const first = list.items[0];
  expect(first).toBeDefined();
  const edit = await erinPage.request.put(`/api/v1/catalogs/${catalogId}/assets/${first?.id}/metadata`, {
    data: {},
    headers: { "X-CSRF-TOKEN": token.token, "If-Match": '"1"' },
  });
  expect(edit.status()).toBe(403);
});
