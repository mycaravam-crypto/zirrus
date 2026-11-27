import { Alert, AspectRatio, Badge, Button, Center, Group, Image, Paper, SimpleGrid, Skeleton, Stack, Text, TextInput, ThemeIcon, UnstyledButton } from "@mantine/core";
import { IconAlertCircle, IconAlertTriangle, IconBan, IconCloudUpload, IconLoader2, IconPhotoOff, IconSearch, IconShieldSearch } from "@tabler/icons-react";
import { useInfiniteQuery } from "@tanstack/react-query";
import type { AssetSummary, ZirrusClient } from "@zirrus/api-client";
import { useState, type ReactNode } from "react";
import { ApiError, assetPath, pendingStates } from "../api";
import { t, type MessageKey } from "../i18n";
import { assetsKey } from "./keys";

/** Badge color per lifecycle state; shades chosen for AA contrast on filled badges. */
export const stateColor: Record<string, string> = {
  uploading: "cyan.9",
  quarantined: "yellow.5",
  processing: "blue.8",
  active: "teal.9",
  rejected: "gray.8",
  failed: "red.9",
};

const stateIcon: Record<string, ReactNode> = {
  uploading: <IconCloudUpload size={30} stroke={1.5} />,
  quarantined: <IconShieldSearch size={30} stroke={1.5} />,
  processing: <IconLoader2 size={30} stroke={1.5} className="spin" />,
  rejected: <IconBan size={30} stroke={1.5} />,
  failed: <IconAlertTriangle size={30} stroke={1.5} />,
};

/** Columns by the grid's own width (container queries), so the detail pane does not squeeze cards. */
const gridCols = { base: 2, "540px": 3, "760px": 4, "1000px": 5 };

/**
 * Search box and thumbnail grid, newest first, with cursor pagination. Polls every 3 s while any
 * visible asset is still being scanned or processed, so states and thumbnails appear without a reload.
 * @param client API client.
 * @param catalogId Catalog to list.
 * @param selected ID of the asset open in the detail pane, if any.
 * @param onSelect Called with an asset ID when a card is chosen.
 */
export function AssetGrid({
  client,
  catalogId,
  selected,
  onSelect,
}: {
  client: ZirrusClient;
  catalogId: string;
  selected: string | null;
  onSelect: (assetId: string) => void;
}) {
  const [draft, setDraft] = useState("");
  const [search, setSearch] = useState("");
  const assets = useAssetPages(client, catalogId, search);

  return (
    <Stack gap="lg">
      <form
        role="search"
        onSubmit={(event) => {
          event.preventDefault();
          setSearch(draft.trim());
        }}
      >
        <Group gap="sm" align="flex-end" wrap="nowrap">
          <TextInput
            id="asset-search"
            type="search"
            label={t("assets.search")}
            maxLength={200}
            value={draft}
            onChange={(event) => setDraft(event.currentTarget.value)}
            leftSection={<IconSearch size={16} aria-hidden="true" />}
            style={{ flex: 1 }}
          />
          <Button type="submit" variant="default">
            {t("assets.searchSubmit")}
          </Button>
        </Group>
      </form>
      <AssetResults assets={assets} catalogId={catalogId} selected={selected} onSelect={onSelect} searching={search !== ""} />
    </Stack>
  );
}

/**
 * Pages of a catalog's assets, newest first, optionally filtered by a search term.
 * @param client API client.
 * @param catalogId Catalog to list.
 * @param search Search term, or empty for all assets.
 * @returns The infinite query.
 */
function useAssetPages(client: ZirrusClient, catalogId: string, search: string) {
  return useInfiniteQuery({
    queryKey: [...assetsKey(catalogId), search],
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }) => {
      const { data, response } = await client.GET("/api/v1/catalogs/{catalogId}/assets", {
        params: { path: { catalogId }, query: { cursor: pageParam, q: search || undefined } },
      });
      if (data === undefined) throw new ApiError(response.status, "Could not load assets");
      return data;
    },
    getNextPageParam: (page) => page.nextCursor ?? undefined,
    refetchInterval: (query) =>
      query.state.data?.pages.some((p) => p.items.some((a) => pendingStates.has(a.state))) ? 3000 : false,
  });
}

/**
 * Loading, error, empty and grid states of the asset list.
 * @param assets The query of asset pages.
 * @param catalogId Catalog being listed.
 * @param selected ID of the asset open in the detail pane, if any.
 * @param onSelect Called with an asset ID when a card is chosen.
 * @param searching Whether a search term is applied (changes the empty message).
 */
function AssetResults({
  assets,
  catalogId,
  selected,
  onSelect,
  searching,
}: {
  assets: ReturnType<typeof useAssetPages>;
  catalogId: string;
  selected: string | null;
  onSelect: (assetId: string) => void;
  searching: boolean;
}) {
  if (assets.isPending) {
    return (
      <>
        <Text role="status" className="sr-only">
          {t("app.loading")}
        </Text>
        <div className="asset-grid-container">
          <SimpleGrid type="container" cols={gridCols} spacing="md" aria-hidden="true">
            {Array.from({ length: 8 }, (_, i) => (
              <Skeleton key={i} radius="md" className="card-skeleton" />
            ))}
          </SimpleGrid>
        </div>
      </>
    );
  }
  if (assets.isError) {
    return (
      <Alert color="red.9" variant="light" icon={<IconAlertCircle />}>
        {t("assets.failed")}
      </Alert>
    );
  }
  const items = assets.data.pages.flatMap((p) => p.items);
  if (items.length === 0) {
    return (
      <Paper p="xl" radius="lg" className="empty-state">
        <Stack align="center" gap="xs">
          <ThemeIcon size={52} radius="xl" variant="light" color="gray" aria-hidden="true">
            {searching ? <IconSearch size={28} stroke={1.5} /> : <IconPhotoOff size={28} stroke={1.5} />}
          </ThemeIcon>
          <Text fw={600}>{t(searching ? "assets.searchEmpty" : "assets.empty")}</Text>
        </Stack>
      </Paper>
    );
  }

  return (
    <Stack gap="lg" align="center">
      <div className="asset-grid-container">
        <SimpleGrid type="container" component="ul" cols={gridCols} spacing="md" className="plain-list asset-grid">
          {items.map((asset) => (
            <li key={asset.id}>
              <UnstyledButton className="asset-card" aria-pressed={asset.id === selected} onClick={() => onSelect(asset.id)}>
                <Thumbnail catalogId={catalogId} asset={asset} />
                <span className="asset-card-body">
                  {/* The accessible name contains the visible text (WCAG 2.5.3): "Open <name> [state]". */}
                  <span className="asset-card-name">
                    <span className="sr-only">{t("assets.openPrefix")}</span> {asset.fileName}
                  </span>
                  {asset.state !== "active" && (
                    <Badge size="sm" variant="filled" color={stateColor[asset.state] ?? "gray.8"} className="asset-card-badge">
                      {stateLabel(asset.state)}
                    </Badge>
                  )}
                </span>
              </UnstyledButton>
            </li>
          ))}
        </SimpleGrid>
      </div>
      {assets.hasNextPage && (
        <Button variant="default" onClick={() => void assets.fetchNextPage()} loading={assets.isFetchingNextPage}>
          {t("assets.more")}
        </Button>
      )}
    </Stack>
  );
}

/**
 * Thumbnail of an active asset, or a state icon while it is processed.
 * @param catalogId Catalog of the asset.
 * @param asset Asset to show.
 */
function Thumbnail({ catalogId, asset }: { catalogId: string; asset: AssetSummary }) {
  return (
    <AspectRatio ratio={1} className="asset-card-thumb">
      {asset.state === "active" ? (
        <Image src={`${assetPath(catalogId, asset.id)}/renditions/thumbnail`} alt="" loading="lazy" width={160} height={160} fit="cover" />
      ) : (
        <Center className={`thumb-placeholder state-${asset.state}`} aria-hidden="true">
          {stateIcon[asset.state] ?? <IconPhotoOff size={30} stroke={1.5} />}
        </Center>
      )}
    </AspectRatio>
  );
}

/**
 * Localized lifecycle state.
 * @param state Lowercase state from the API, e.g. `processing`.
 * @returns The label in the UI language.
 */
export function stateLabel(state: string) {
  return t(`state.${state}` as MessageKey);
}
