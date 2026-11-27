import { useQuery } from "@tanstack/react-query";
import type { CatalogSummary, ZirrusClient } from "@zirrus/api-client";
import { useState } from "react";
import { ApiError } from "../api";
import { t, type MessageKey } from "../i18n";
import { AssetDetail } from "./AssetDetail";
import { AssetGrid } from "./AssetGrid";
import { UploadPanel } from "./UploadPanel";

/**
 * Catalog navigation, asset grid, upload and detail pane (W6 explorer).
 * @param client API client.
 */
export function Explorer({ client }: { client: ZirrusClient }) {
  const [catalogId, setCatalogId] = useState<string | null>(null);
  const [assetId, setAssetId] = useState<string | null>(null);
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
    <div className="explorer">
      <nav className="catalogs" aria-labelledby="catalogs-heading">
        <h2 id="catalogs-heading">{t("catalogs.heading")}</h2>
        {catalogs.isPending && <p role="status">{t("app.loading")}</p>}
        {catalogs.isError && <p role="alert">{t("catalogs.failed")}</p>}
        {catalogs.isSuccess && catalogs.data.length === 0 && <p>{t("catalogs.empty")}</p>}
        {catalogs.isSuccess && catalogs.data.length > 0 && (
          <ul>
            {catalogs.data.map((c) => (
              <li key={c.id}>
                <button
                  type="button"
                  aria-current={c.id === catalogId ? "page" : undefined}
                  onClick={() => {
                    setCatalogId(c.id);
                    setAssetId(null);
                  }}
                >
                  {c.name} <span className="access">({accessLabel(c)})</span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </nav>

      <section className="assets" aria-labelledby="assets-heading">
        {catalog === null ? (
          <p>{t("catalogs.choose")}</p>
        ) : (
          <>
            <h2 id="assets-heading">{t("assets.heading", { catalog: catalog.name })}</h2>
            {canEdit && <UploadPanel client={client} catalogId={catalog.id} />}
            <AssetGrid key={catalog.id} client={client} catalogId={catalog.id} selected={assetId} onSelect={setAssetId} />
          </>
        )}
      </section>

      {catalog !== null && assetId !== null && (
        <AssetDetail client={client} catalogId={catalog.id} assetId={assetId} canEdit={canEdit} onClose={() => setAssetId(null)} />
      )}
    </div>
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
