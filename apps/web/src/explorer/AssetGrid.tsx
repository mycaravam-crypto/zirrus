import { useInfiniteQuery } from "@tanstack/react-query";
import type { AssetSummary, ZirrusClient } from "@zirrus/api-client";
import { ApiError, assetPath, pendingStates } from "../api";
import { t, type MessageKey } from "../i18n";
import { assetsKey } from "./keys";

/**
 * Thumbnail grid, newest first, with cursor pagination. Polls every 3 s while any visible asset is
 * still being scanned or processed, so states and thumbnails appear without a reload.
 * @param props Component props.
 * @param props.client API client.
 * @param props.catalogId Catalog to list.
 * @param props.selected ID of the asset open in the detail pane, if any.
 * @param props.onSelect Called with an asset ID when a card is chosen.
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
  const assets = useInfiniteQuery({
    queryKey: assetsKey(catalogId),
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }) => {
      const { data, response } = await client.GET("/api/v1/catalogs/{catalogId}/assets", {
        params: { path: { catalogId }, query: { cursor: pageParam } },
      });
      if (data === undefined) throw new ApiError(response.status, "Could not load assets");
      return data;
    },
    getNextPageParam: (page) => page.nextCursor ?? undefined,
    refetchInterval: (query) =>
      query.state.data?.pages.some((p) => p.items.some((a) => pendingStates.has(a.state))) ? 3000 : false,
  });

  if (assets.isPending) return <p role="status">{t("app.loading")}</p>;
  if (assets.isError) return <p role="alert">{t("assets.failed")}</p>;
  const items = assets.data.pages.flatMap((p) => p.items);
  if (items.length === 0) return <p>{t("assets.empty")}</p>;

  return (
    <>
      <ul className="grid">
        {items.map((asset) => (
          <li key={asset.id}>
            <button
              type="button"
              className="card"
              aria-pressed={asset.id === selected}
              onClick={() => onSelect(asset.id)}
            >
              <Thumbnail catalogId={catalogId} asset={asset} />
              {/* The accessible name contains the visible text (WCAG 2.5.3): "Open <name> [state]". */}
              <span className="name">
                <span className="sr-only">{t("assets.openPrefix")}</span>{" "}
                {asset.fileName}
              </span>
              {asset.state !== "active" && <span className={`badge state-${asset.state}`}>{stateLabel(asset.state)}</span>}
            </button>
          </li>
        ))}
      </ul>
      {assets.hasNextPage && (
        <button type="button" onClick={() => void assets.fetchNextPage()} disabled={assets.isFetchingNextPage}>
          {t("assets.more")}
        </button>
      )}
    </>
  );
}

/**
 * Thumbnail of an active asset, or a placeholder while it is processed.
 * @param props Component props.
 * @param props.catalogId Catalog of the asset.
 * @param props.asset Asset to show.
 */
function Thumbnail({ catalogId, asset }: { catalogId: string; asset: AssetSummary }) {
  if (asset.state !== "active") return <span className="thumb placeholder" aria-hidden="true" />;
  return <img className="thumb" src={`${assetPath(catalogId, asset.id)}/renditions/thumbnail`} alt="" loading="lazy" width={160} height={160} />;
}

/**
 * Localized lifecycle state.
 * @param state Lowercase state from the API, e.g. `processing`.
 * @returns The label in the UI language.
 */
export function stateLabel(state: string) {
  return t(`state.${state}` as MessageKey);
}
