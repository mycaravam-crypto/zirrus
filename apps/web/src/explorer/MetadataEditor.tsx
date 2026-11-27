import { useMutation, useQueryClient } from "@tanstack/react-query";
import type { AssetDetails, ZirrusClient } from "@zirrus/api-client";
import { useId, useState } from "react";
import { ApiError, csrfToken, problemTitle } from "../api";
import { t } from "../i18n";
import { assetKey } from "./keys";

type Row = { id: number; key: string; value: string };

const keyPattern = /^[a-z][a-z0-9_]{0,63}$/;

/**
 * Edits an asset's metadata as text fields. Saves send If-Match with the revision the form was
 * loaded from; a 412 means someone else saved first, and the user reloads instead of overwriting.
 * Structured (non-text) values are kept unchanged until typed fields arrive with W5 (#25).
 * @param client API client.
 * @param catalogId Catalog of the asset.
 * @param asset Asset with its current revision and metadata.
 * @param canEdit Whether the user may edit (editor or administrator).
 */
export function MetadataEditor({
  client,
  catalogId,
  asset,
  canEdit,
}: {
  client: ZirrusClient;
  catalogId: string;
  asset: AssetDetails;
  canEdit: boolean;
}) {
  // Remount (and reset the form) whenever a new revision is loaded; remember which one we saved.
  const [savedRevision, setSavedRevision] = useState<number | null>(null);
  return (
    <Form
      key={`${asset.id}:${asset.revision}`}
      client={client}
      catalogId={catalogId}
      asset={asset}
      canEdit={canEdit}
      saved={savedRevision === Number(asset.revision)}
      onSaved={setSavedRevision}
    />
  );
}

/**
 * The editable form for one revision; remounted when a new revision loads.
 * @param client API client.
 * @param catalogId Catalog of the asset.
 * @param asset Asset at the revision being edited.
 * @param canEdit Whether the user may edit; otherwise a read-only list is shown.
 * @param saved Whether this revision is the one the user just saved.
 * @param onSaved Called with the new revision after a successful save.
 */
function Form({
  client,
  catalogId,
  asset,
  canEdit,
  saved,
  onSaved,
}: {
  client: ZirrusClient;
  catalogId: string;
  asset: AssetDetails;
  canEdit: boolean;
  saved: boolean;
  onSaved: (revision: number) => void;
}) {
  const queryClient = useQueryClient();
  const formId = useId();
  const metadata = (asset.metadata ?? {}) as Record<string, unknown>;
  const structured = Object.entries(metadata).filter(([, v]) => typeof v !== "string");
  const [rows, setRows] = useState<Row[]>(() =>
    Object.entries(metadata)
      .filter(([, v]) => typeof v === "string")
      .map(([key, value], id) => ({ id, key, value: value as string })),
  );
  const [nextId, setNextId] = useState(rows.length);
  const [conflict, setConflict] = useState(false);

  const keys = rows.map((r) => r.key.trim());
  const invalid = rows.find((r) => !keyPattern.test(r.key.trim()));
  const duplicate = keys.find((k, i) => keys.indexOf(k) !== i);

  const save = useMutation({
    mutationFn: async () => {
      const body: Record<string, unknown> = Object.fromEntries(structured);
      for (const row of rows) body[row.key.trim()] = row.value;
      const token = await csrfToken(client);
      const { data, error, response } = await client.PUT("/api/v1/catalogs/{catalogId}/assets/{assetId}/metadata", {
        params: { path: { catalogId, assetId: asset.id }, header: { "If-Match": `"${asset.revision}"` } },
        headers: { "X-CSRF-TOKEN": token },
        body,
      });
      if (data === undefined) throw new ApiError(response.status, problemTitle(error, `HTTP ${response.status}`));
      return data;
    },
    onSuccess: (data) => {
      onSaved(Number(data.revision));
      queryClient.setQueryData(assetKey(catalogId, asset.id), data);
    },
    onError: (error) => setConflict(error instanceof ApiError && error.status === 412),
  });

  if (!canEdit) {
    return (
      <section aria-labelledby={`${formId}-heading`}>
        <h3 id={`${formId}-heading`}>{t("metadata.heading")}</h3>
        <p>{t("metadata.readOnly")}</p>
        <dl className="metadata">
          {Object.entries(metadata).map(([key, value]) => (
            <div key={key}>
              <dt>{key}</dt>
              <dd>{typeof value === "string" ? value : JSON.stringify(value)}</dd>
            </div>
          ))}
        </dl>
      </section>
    );
  }

  return (
    <form
      aria-labelledby={`${formId}-heading`}
      onSubmit={(event) => {
        event.preventDefault();
        if (invalid === undefined && duplicate === undefined) save.mutate();
      }}
    >
      <h3 id={`${formId}-heading`}>{t("metadata.heading")}</h3>
      {rows.map((row) => (
        <div className="field-row" key={row.id}>
          <label>
            {t("metadata.key")}
            <input
              value={row.key}
              aria-invalid={!keyPattern.test(row.key.trim())}
              onChange={(e) => setRows(rows.map((r) => (r.id === row.id ? { ...r, key: e.target.value } : r)))}
            />
          </label>
          <label>
            {t("metadata.value")}
            <input value={row.value} onChange={(e) => setRows(rows.map((r) => (r.id === row.id ? { ...r, value: e.target.value } : r)))} />
          </label>
          <button type="button" aria-label={t("metadata.remove", { key: row.key || "…" })} onClick={() => setRows(rows.filter((r) => r.id !== row.id))}>
            {t("metadata.removeShort")}
          </button>
        </div>
      ))}
      {structured.map(([key, value]) => (
        <p key={key} className="structured">
          <strong>{key}</strong>: <code>{JSON.stringify(value)}</code> <span>({t("metadata.structured")})</span>
        </p>
      ))}
      {invalid !== undefined && <p role="alert">{t("metadata.invalidKey")}</p>}
      {duplicate !== undefined && <p role="alert">{t("metadata.duplicateKey", { key: duplicate })}</p>}
      <div className="actions">
        <button
          type="button"
          onClick={() => {
            setRows([...rows, { id: nextId, key: "", value: "" }]);
            setNextId(nextId + 1);
          }}
        >
          {t("metadata.add")}
        </button>
        <button type="submit" disabled={save.isPending || invalid !== undefined || duplicate !== undefined}>
          {t("metadata.save")}
        </button>
      </div>
      {saved && <p role="status">{t("metadata.saved")}</p>}
      {conflict && (
        <div role="alert" className="conflict">
          <p>{t("metadata.conflict")}</p>
          <button type="button" onClick={() => void queryClient.invalidateQueries({ queryKey: assetKey(catalogId, asset.id) })}>
            {t("metadata.reload")}
          </button>
        </div>
      )}
      {save.isError && !conflict && <p role="alert">{t("metadata.failed", { reason: save.error.message })}</p>}
    </form>
  );
}
