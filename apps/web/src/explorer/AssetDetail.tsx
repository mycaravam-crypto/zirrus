import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { AssetDetails, ZirrusClient } from "@zirrus/api-client";
import { useEffect, useId, useRef } from "react";
import { ApiError, assetPath, csrfToken, formatBytes, pendingStates, problemTitle } from "../api";
import { t } from "../i18n";
import { stateLabel } from "./AssetGrid";
import { assetKey, assetsKey } from "./keys";
import { MetadataEditor } from "./MetadataEditor";

/**
 * Detail pane: preview, file facts, download, retry and the metadata editor.
 * @param client API client.
 * @param catalogId Catalog of the asset.
 * @param assetId Asset to show.
 * @param canEdit Whether the user may retry and edit (editor or administrator).
 * @param onClose Called when the user closes the pane.
 */
export function AssetDetail({
  client,
  catalogId,
  assetId,
  canEdit,
  onClose,
}: {
  client: ZirrusClient;
  catalogId: string;
  assetId: string;
  canEdit: boolean;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const headingId = useId();
  const heading = useRef<HTMLHeadingElement>(null);
  const asset = useQuery({
    queryKey: assetKey(catalogId, assetId),
    queryFn: async (): Promise<AssetDetails> => {
      const { data, response } = await client.GET("/api/v1/catalogs/{catalogId}/assets/{assetId}", {
        params: { path: { catalogId, assetId } },
      });
      if (data === undefined) throw new ApiError(response.status, "Could not load the asset");
      return data;
    },
    refetchInterval: (query) => (query.state.data && pendingStates.has(query.state.data.state) ? 3000 : false),
  });

  // Move focus to the pane when it opens, so keyboard and screen-reader users land on it.
  useEffect(() => heading.current?.focus(), [assetId, asset.isSuccess]);

  const retry = useMutation({
    mutationFn: async () => {
      const token = await csrfToken(client);
      const { data, error, response } = await client.POST("/api/v1/catalogs/{catalogId}/assets/{assetId}/retry", {
        params: { path: { catalogId, assetId } },
        headers: { "X-CSRF-TOKEN": token },
      });
      if (data === undefined) throw new ApiError(response.status, problemTitle(error, `HTTP ${response.status}`));
      return data;
    },
    onSuccess: async (data) => {
      queryClient.setQueryData(assetKey(catalogId, assetId), data);
      await queryClient.invalidateQueries({ queryKey: assetsKey(catalogId) });
    },
  });

  return (
    <aside className="detail" aria-labelledby={headingId}>
      <button type="button" className="close" onClick={onClose}>
        {t("detail.close")}
      </button>
      {asset.isPending && <p role="status">{t("app.loading")}</p>}
      {asset.isError && <p role="alert">{t("detail.failed")}</p>}
      {asset.isSuccess && (
        <>
          <h2 id={headingId} ref={heading} tabIndex={-1}>
            {t("detail.heading", { name: asset.data.fileName })}
          </h2>
          {asset.data.state === "active" ? (
            <img className="preview" src={`${assetPath(catalogId, assetId)}/renditions/preview`} alt={t("detail.preview", { name: asset.data.fileName })} />
          ) : (
            <p className="no-preview">{t("detail.noPreview")}</p>
          )}
          <dl className="facts">
            <dt>{t("detail.state")}</dt>
            <dd>{stateLabel(asset.data.state)}</dd>
            {asset.data.statusReason && (
              <>
                <dt>{t("detail.reason")}</dt>
                <dd>{asset.data.statusReason}</dd>
              </>
            )}
            <dt>{t("detail.type")}</dt>
            <dd>{asset.data.mediaType}</dd>
            <dt>{t("detail.size")}</dt>
            <dd>{formatBytes(asset.data.sizeBytes)}</dd>
            <dt>{t("detail.checksum")}</dt>
            <dd className="checksum">{asset.data.sha256 ?? "–"}</dd>
            <dt>{t("detail.uploaded")}</dt>
            <dd>
              <time dateTime={asset.data.createdAt}>{new Date(asset.data.createdAt).toLocaleString()}</time>
            </dd>
          </dl>
          <div className="actions">
            {asset.data.state === "active" && (
              <a href={`${assetPath(catalogId, assetId)}/content`} download={asset.data.fileName}>
                {t("detail.download")}
              </a>
            )}
            {canEdit && (asset.data.state === "failed" || asset.data.state === "quarantined") && (
              <button type="button" onClick={() => retry.mutate()} disabled={retry.isPending}>
                {t("detail.retry")}
              </button>
            )}
          </div>
          {retry.isError && <p role="alert">{t("detail.retryFailed", { reason: retry.error.message })}</p>}
          <MetadataEditor client={client} catalogId={catalogId} asset={asset.data} canEdit={canEdit} />
        </>
      )}
    </aside>
  );
}
