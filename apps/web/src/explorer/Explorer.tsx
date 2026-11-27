import { Alert, AppShell, Badge, Center, Group, NavLink, ScrollArea, Skeleton, Stack, Text, ThemeIcon, Title } from "@mantine/core";
import { IconAlertCircle, IconFolder, IconFolderOpen, IconLayoutGrid } from "@tabler/icons-react";
import { useQuery } from "@tanstack/react-query";
import type { CatalogSummary, ZirrusClient } from "@zirrus/api-client";
import { useState } from "react";
import { ApiError } from "../api";
import { t, type MessageKey } from "../i18n";
import { AssetDetail } from "./AssetDetail";
import { AssetGrid } from "./AssetGrid";
import { CategoryNav } from "./CategoryNav";
import { UploadPanel } from "./UploadPanel";

const accessColor: Record<string, string> = { admin: "grape.9", editor: "brand", viewer: "gray.7" };

/**
 * Catalog navigation, asset grid, upload and detail pane (W6 explorer).
 * @param client API client.
 * @param onNavigate Called after a catalog is chosen (closes the navigation on phones).
 */
export function Explorer({ client, onNavigate }: { client: ZirrusClient; onNavigate: () => void }) {
  const [catalogId, setCatalogId] = useState<string | null>(null);
  const [assetId, setAssetId] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  const [categoryId, setCategoryId] = useState<string | null>(null);
  const catalogs = useQuery({
    queryKey: ["catalogs"],
    queryFn: async () => {
      const { data, response } = await client.GET("/api/v1/catalogs");
      if (data === undefined) throw new ApiError(response.status, "Could not load catalogs");
      return data;
    },
  });
  const catalog = catalogs.data?.find((c) => c.id === catalogId) ?? null;
  const canEdit = catalog !== null && catalog.access !== "viewer";

  return (
    <>
      <AppShell.Navbar p="md" component="nav" aria-labelledby="catalogs-heading">
        <Title order={2} size="xs" tt="uppercase" c="dimmed" fw={700} mb="sm" id="catalogs-heading" className="nav-heading">
          {t("catalogs.heading")}
        </Title>
        <AppShell.Section grow component={ScrollArea}>
          {catalogs.isPending && (
            <Stack gap="xs">
              <Text role="status" className="sr-only">
                {t("app.loading")}
              </Text>
              {[0, 1, 2].map((i) => (
                <Skeleton key={i} h={40} />
              ))}
            </Stack>
          )}
          {catalogs.isError && (
            <Alert color="red.9" variant="light" icon={<IconAlertCircle />}>
              {t("catalogs.failed")}
            </Alert>
          )}
          {catalogs.isSuccess && catalogs.data.length === 0 && <Text c="dimmed">{t("catalogs.empty")}</Text>}
          {catalogs.isSuccess && catalogs.data.length > 0 && (
            <ul className="plain-list">
              {catalogs.data.map((c) => {
                const active = c.id === catalogId;
                return (
                  <li key={c.id}>
                    <NavLink
                      component="button"
                      type="button"
                      active={active}
                      aria-current={active ? "page" : undefined}
                      variant="light"
                      className="catalog-link"
                      leftSection={active ? <IconFolderOpen size={18} stroke={1.6} /> : <IconFolder size={18} stroke={1.6} />}
                      label={c.name}
                      rightSection={
                        <Badge size="xs" variant="filled" color={accessColor[c.access] ?? "gray.7"}>
                          {accessLabel(c)}
                        </Badge>
                      }
                      onClick={() => {
                        setCatalogId(c.id);
                        setAssetId(null);
                        setSearch("");
                        setCategoryId(null);
                        onNavigate();
                      }}
                    />
                  </li>
                );
              })}
            </ul>
          )}
          {catalog !== null && (
            <CategoryNav
              key={catalog.id}
              client={client}
              catalogId={catalog.id}
              search={search}
              selected={categoryId}
              onSelect={(id) => {
                setCategoryId(id);
                onNavigate();
              }}
              canAdminister={catalog.access === "admin"}
            />
          )}
        </AppShell.Section>
      </AppShell.Navbar>

      <AppShell.Main>
        <div className={`explorer${assetId !== null ? " with-detail" : ""}`}>
          <section className="assets" aria-labelledby="assets-heading">
            {catalog === null ? (
              <Center mih="50vh">
                <Stack align="center" gap="sm" ta="center" maw={360}>
                  <ThemeIcon size={64} radius="xl" variant="light" aria-hidden="true">
                    <IconLayoutGrid size={32} stroke={1.5} />
                  </ThemeIcon>
                  <Text size="lg" fw={600}>
                    {t("catalogs.choose")}
                  </Text>
                </Stack>
              </Center>
            ) : (
              <Stack gap="lg">
                <Group justify="space-between" align="flex-end">
                  <div>
                    <Title order={2} id="assets-heading">
                      {t("assets.heading", { catalog: catalog.name })}
                    </Title>
                    <Text c="dimmed" size="sm">
                      {canEdit ? t("assets.subtitleEditor") : t("assets.subtitleViewer")}
                    </Text>
                  </div>
                </Group>
                {canEdit && <UploadPanel client={client} catalogId={catalog.id} />}
                <AssetGrid
                  key={catalog.id}
                  client={client}
                  catalogId={catalog.id}
                  search={search}
                  onSearch={setSearch}
                  categoryId={categoryId}
                  onClearCategory={() => setCategoryId(null)}
                  selected={assetId}
                  onSelect={setAssetId}
                />
              </Stack>
            )}
          </section>

          {catalog !== null && assetId !== null && (
            <AssetDetail client={client} catalogId={catalog.id} assetId={assetId} canEdit={canEdit} onClose={() => setAssetId(null)} />
          )}
        </div>
      </AppShell.Main>
    </>
  );
}

/**
 * Localized access level of a catalog.
 * @param catalog Catalog with the caller's access.
 * @returns The label in the UI language.
 */
function accessLabel(catalog: CatalogSummary) {
  return t(`catalogs.access.${catalog.access}` as MessageKey);
}
