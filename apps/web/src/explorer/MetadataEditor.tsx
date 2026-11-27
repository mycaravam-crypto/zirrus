import { ActionIcon, Alert, Button, Code, Group, Loader, Stack, Text, TextInput } from "@mantine/core";
import { IconAlertCircle, IconDeviceFloppy, IconEye, IconPlus, IconTrash } from "@tabler/icons-react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import type { AssetDetails, ZirrusClient } from "@zirrus/api-client";
import { useId, useState } from "react";
import { ApiError } from "../api";
import { t } from "../i18n";
import { assetKey, schemaKey } from "./keys";
import { MetadataHeading, SaveFeedback, useSaveMetadata } from "./saveMetadata";
import { TypedMetadataForm } from "./TypedMetadataForm";

type Row = { id: number; key: string; value: string };

const keyPattern = /^[a-z][a-z0-9_]{0,63}$/;

/**
 * Edits an asset's metadata. With a catalog schema it shows typed fields (TypedMetadataForm);
 * without one, free key/value text rows, keeping structured (non-text) values unchanged. Saves send
 * If-Match with the revision the form was loaded from; a 412 means someone else saved first, and the
 * user reloads instead of overwriting.
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
  const schema = useQuery({
    queryKey: schemaKey(catalogId),
    queryFn: async () => {
      const { data, response } = await client.GET("/api/v1/catalogs/{catalogId}/metadata-schema", { params: { path: { catalogId } } });
      if (data === undefined) throw new ApiError(response.status, "Could not load the metadata schema");
      return data;
    },
  });

  if (schema.isPending) return <Loader size="sm" aria-label={t("app.loading")} />;
  if (schema.isSuccess && Number(schema.data.version) > 0) {
    return (
      <TypedMetadataForm
        key={`${asset.id}:${asset.revision}`}
        client={client}
        catalogId={catalogId}
        asset={asset}
        schema={schema.data}
        canEdit={canEdit}
        saved={savedRevision === Number(asset.revision)}
        onSaved={setSavedRevision}
      />
    );
  }

  // Without a schema (or if it cannot be loaded) the free-form editor keeps working.
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
  const { save, conflict } = useSaveMetadata(client, catalogId, asset, onSaved);

  const keys = rows.map((r) => r.key.trim());
  const invalid = rows.find((r) => !keyPattern.test(r.key.trim()));
  const duplicate = keys.find((k, i) => keys.indexOf(k) !== i);

  const body = () => {
    const result: Record<string, unknown> = Object.fromEntries(structured);
    for (const row of rows) result[row.key.trim()] = row.value;
    return result;
  };

  const heading = <MetadataHeading id={`${formId}-heading`} revision={asset.revision} />;

  if (!canEdit) {
    const entries = Object.entries(metadata);
    return (
      <Stack component="section" aria-labelledby={`${formId}-heading`} gap="sm">
        {heading}
        <Alert variant="light" color="gray" icon={<IconEye />} p="xs">
          <Text size="sm">{t("metadata.readOnly")}</Text>
        </Alert>
        {entries.length === 0 ? (
          <Text size="sm" c="dimmed">
            {t("metadata.empty")}
          </Text>
        ) : (
          <dl className="metadata-table">
            {entries.map(([key, value]) => (
              <div key={key} className="metadata-row">
                <dt>{key}</dt>
                <dd>{typeof value === "string" ? value : JSON.stringify(value)}</dd>
              </div>
            ))}
          </dl>
        )}
      </Stack>
    );
  }

  return (
    <form
      aria-labelledby={`${formId}-heading`}
      onSubmit={(event) => {
        event.preventDefault();
        if (invalid === undefined && duplicate === undefined) save.mutate(body());
      }}
    >
      <Stack gap="sm">
        {heading}
        {rows.length > 0 && (
          <div className="field-grid" aria-hidden="true">
            <Text size="xs" fw={700} c="dimmed" tt="uppercase">
              {t("metadata.key")}
            </Text>
            <Text size="xs" fw={700} c="dimmed" tt="uppercase">
              {t("metadata.value")}
            </Text>
            <span />
          </div>
        )}
        {rows.length === 0 && structured.length === 0 && (
          <Text size="sm" c="dimmed">
            {t("metadata.empty")}
          </Text>
        )}
        {rows.map((row) => (
          <div className="field-grid" key={row.id}>
            <TextInput
              aria-label={t("metadata.key")}
              value={row.key}
              size="sm"
              ff="monospace"
              error={!keyPattern.test(row.key.trim())}
              onChange={(e) => setRows(rows.map((r) => (r.id === row.id ? { ...r, key: e.target.value } : r)))}
            />
            <TextInput
              aria-label={t("metadata.value")}
              value={row.value}
              size="sm"
              onChange={(e) => setRows(rows.map((r) => (r.id === row.id ? { ...r, value: e.target.value } : r)))}
            />
            <ActionIcon
              variant="subtle"
              color="gray.7"
              size="lg"
              aria-label={t("metadata.remove", { key: row.key || "…" })}
              onClick={() => setRows(rows.filter((r) => r.id !== row.id))}
            >
              <IconTrash size={16} />
            </ActionIcon>
          </div>
        ))}
        {structured.map(([key, value]) => (
          <Group key={key} gap="xs" className="structured" wrap="wrap">
            <Text size="sm" fw={600} ff="monospace">
              {key}
            </Text>
            <Code>{JSON.stringify(value)}</Code>
            <Text size="xs" c="dimmed" component="span">
              ({t("metadata.structured")})
            </Text>
          </Group>
        ))}
        {invalid !== undefined && (
          <Alert color="red.9" variant="light" icon={<IconAlertCircle />} p="xs">
            <Text size="sm">{t("metadata.invalidKey")}</Text>
          </Alert>
        )}
        {duplicate !== undefined && (
          <Alert color="red.9" variant="light" icon={<IconAlertCircle />} p="xs">
            <Text size="sm">{t("metadata.duplicateKey", { key: duplicate })}</Text>
          </Alert>
        )}
        <Group justify="space-between" mt={4}>
          <Button
            variant="default"
            size="sm"
            leftSection={<IconPlus size={16} />}
            onClick={() => {
              setRows([...rows, { id: nextId, key: "", value: "" }]);
              setNextId(nextId + 1);
            }}
          >
            {t("metadata.add")}
          </Button>
          <Button
            type="submit"
            size="sm"
            leftSection={<IconDeviceFloppy size={16} />}
            loading={save.isPending}
            disabled={invalid !== undefined || duplicate !== undefined}
          >
            {t("metadata.save")}
          </Button>
        </Group>
        <SaveFeedback
          saved={saved}
          conflict={conflict}
          error={save.error}
          onReload={() => void queryClient.invalidateQueries({ queryKey: assetKey(catalogId, asset.id) })}
        />
      </Stack>
    </form>
  );
}
