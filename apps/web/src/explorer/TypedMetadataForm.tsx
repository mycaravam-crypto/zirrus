import { Alert, Badge, Button, Group, Select, Stack, TagsInput, Text, Textarea, TextInput } from "@mantine/core";
import { IconDeviceFloppy, IconEye, IconInfoCircle } from "@tabler/icons-react";
import { useQueryClient } from "@tanstack/react-query";
import type { AssetDetails, MetadataFieldDefinition, SchemaDetails, ZirrusClient } from "@zirrus/api-client";
import { useId, useState } from "react";
import { t } from "../i18n";
import { assetKey } from "./keys";
import { MetadataHeading, SaveFeedback, useSaveMetadata } from "./saveMetadata";

/** MetadataFieldType from the API (an integer in the OpenAPI contract). */
const FieldType = { Text: 0, Keyword: 1, Number: 2, Date: 3, Boolean: 4 } as const;

type Value = string | string[];

const datePattern = /^\d{4}-\d{2}-\d{2}$/;
const timestampPattern = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$/;

/**
 * Metadata form generated from the catalog's schema: one control per field, required fields marked,
 * values checked as the server does (numbers, dates with an explicit offset, booleans, lists).
 * @param client API client.
 * @param catalogId Catalog of the asset.
 * @param asset Asset at the revision being edited.
 * @param schema Current catalog schema (version ≥ 1).
 * @param canEdit Whether the user may edit; otherwise the values are listed read-only.
 * @param saved Whether this revision is the one the user just saved.
 * @param onSaved Called with the new revision after a successful save.
 */
export function TypedMetadataForm({
  client,
  catalogId,
  asset,
  schema,
  canEdit,
  saved,
  onSaved,
}: {
  client: ZirrusClient;
  catalogId: string;
  asset: AssetDetails;
  schema: SchemaDetails;
  canEdit: boolean;
  saved: boolean;
  onSaved: (revision: number) => void;
}) {
  const queryClient = useQueryClient();
  const formId = useId();
  const metadata = (asset.metadata ?? {}) as Record<string, unknown>;
  const fields = schema.fields;
  const [values, setValues] = useState<Record<string, Value>>(() =>
    Object.fromEntries(fields.map((f) => [f.key, initialValue(f, metadata[f.key])])),
  );
  const [attempted, setAttempted] = useState(false);
  const { save, conflict } = useSaveMetadata(client, catalogId, asset, onSaved);
  const unknown = Object.keys(metadata).filter((key) => !fields.some((f) => f.key === key));
  const errors = Object.fromEntries(fields.map((f) => [f.key, validate(f, values[f.key] ?? "")]));
  const hasErrors = Object.values(errors).some((e) => e !== null);
  const heading = (
    <MetadataHeading id={`${formId}-heading`} revision={asset.revision}>
      <Badge variant="light" size="sm" radius="sm" title={t("typed.schemaNote", { version: String(schema.version) })}>
        {`Schema v${schema.version}`}
      </Badge>
    </MetadataHeading>
  );

  if (!canEdit) {
    return (
      <Stack component="section" aria-labelledby={`${formId}-heading`} gap="sm">
        {heading}
        <Alert variant="light" color="gray" icon={<IconEye />} p="xs">
          <Text size="sm">{t("metadata.readOnly")}</Text>
        </Alert>
        <dl className="metadata-table">
          {fields.map((f) => (
            <div key={f.key} className="metadata-row">
              <dt>{f.label}</dt>
              <dd>{display(f, metadata[f.key])}</dd>
            </div>
          ))}
        </dl>
      </Stack>
    );
  }

  const set = (key: string, value: Value) => setValues((current) => ({ ...current, [key]: value }));

  return (
    <form
      aria-labelledby={`${formId}-heading`}
      noValidate
      onSubmit={(event) => {
        event.preventDefault();
        setAttempted(true);
        if (!hasErrors) save.mutate(toBody(fields, values));
      }}
    >
      <Stack gap="sm">
        {heading}
        {fields.map((field) => (
          <FieldInput
            key={field.key}
            field={field}
            value={values[field.key] ?? ""}
            error={attempted ? errors[field.key] : null}
            onChange={(value) => set(field.key, value)}
          />
        ))}
        {unknown.length > 0 && (
          <Alert color="yellow" variant="light" icon={<IconInfoCircle />} p="xs">
            <Text size="sm">{t("typed.unknown", { keys: unknown.join(", ") })}</Text>
          </Alert>
        )}
        <Group justify="flex-end" mt={4}>
          <Button type="submit" size="sm" leftSection={<IconDeviceFloppy size={16} />} loading={save.isPending}>
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

/**
 * The control for one schema field.
 * @param field Field definition.
 * @param value Current value: a string, or a list for multiple fields.
 * @param error Validation message to show, if any.
 * @param onChange Called with the new value.
 */
function FieldInput({ field, value, error, onChange }: { field: MetadataFieldDefinition; value: Value; error: string | null | undefined; onChange: (value: Value) => void }) {
  const common = { label: field.label, withAsterisk: field.required === true, error: error ?? undefined, size: "sm" as const };
  if (field.multiple === true) {
    return (
      <TagsInput
        {...common}
        description={t("typed.multipleHint")}
        value={Array.isArray(value) ? value : []}
        onChange={onChange}
        clearable
        comboboxProps={{ withinPortal: false }}
      />
    );
  }

  const text = typeof value === "string" ? value : "";
  switch (Number(field.type)) {
    case FieldType.Boolean:
      return (
        <Select
          {...common}
          data={[
            { value: "true", label: t("typed.yes") },
            { value: "false", label: t("typed.no") },
          ]}
          value={text === "" ? null : text}
          onChange={(v) => onChange(v ?? "")}
          clearable={field.required !== true}
          comboboxProps={{ withinPortal: false }}
        />
      );
    case FieldType.Number:
      return <TextInput {...common} inputMode="decimal" value={text} onChange={(e) => onChange(e.currentTarget.value)} />;
    case FieldType.Date:
      return <TextInput {...common} description={t("typed.dateHint")} placeholder="2026-11-27" value={text} onChange={(e) => onChange(e.currentTarget.value)} />;
    case FieldType.Text:
      return <Textarea {...common} autosize minRows={1} maxRows={6} value={text} onChange={(e) => onChange(e.currentTarget.value)} />;
    default:
      return <TextInput {...common} value={text} onChange={(e) => onChange(e.currentTarget.value)} />;
  }
}

/**
 * Form value for a stored metadata value.
 * @param field Field definition.
 * @param stored Value from the asset's metadata, if any.
 * @returns A string, or a list of strings for multiple fields.
 */
function initialValue(field: MetadataFieldDefinition, stored: unknown): Value {
  const asText = (v: unknown) => (typeof v === "string" ? v : v === null || v === undefined ? "" : String(v));
  if (field.multiple === true) return Array.isArray(stored) ? stored.map(asText) : stored === undefined || stored === null ? [] : [asText(stored)];
  return Array.isArray(stored) ? stored.map(asText).join(", ") : asText(stored);
}

/**
 * Checks one value the way the server does.
 * @param field Field definition.
 * @param value Form value.
 * @returns A message, or null when valid.
 */
function validate(field: MetadataFieldDefinition, value: Value): string | null {
  const items = (Array.isArray(value) ? value : [value]).map((v) => v.trim()).filter((v) => v !== "");
  if (items.length === 0) return field.required === true ? t("typed.missing", { label: field.label }) : null;
  const type = Number(field.type);
  if (type === FieldType.Number && items.some((v) => !Number.isFinite(Number(v)))) return t("typed.invalidNumber", { label: field.label });
  if (type === FieldType.Date && items.some((v) => !(datePattern.test(v) || timestampPattern.test(v)) || Number.isNaN(Date.parse(v))))
    return t("typed.invalidDate", { label: field.label });
  return null;
}

/**
 * Request body: schema fields only (unknown keys are dropped, as the server would reject them),
 * empty optional fields omitted, values converted to their JSON types.
 * @param fields Schema fields.
 * @param values Form values.
 * @returns The metadata object to save.
 */
function toBody(fields: readonly MetadataFieldDefinition[], values: Record<string, Value>) {
  const convert = (field: MetadataFieldDefinition, v: string): unknown => {
    const type = Number(field.type);
    if (type === FieldType.Number) return Number(v);
    if (type === FieldType.Boolean) return v === "true";
    return v;
  };
  const body: Record<string, unknown> = {};
  for (const field of fields) {
    const raw = values[field.key] ?? "";
    const items = (Array.isArray(raw) ? raw : [raw]).map((v) => v.trim()).filter((v) => v !== "");
    if (items.length === 0) continue;
    body[field.key] = field.multiple === true ? items.map((v) => convert(field, v)) : convert(field, items[0] ?? "");
  }
  return body;
}

/**
 * Read-only display of a stored value.
 * @param field Field definition.
 * @param stored Stored value.
 * @returns Text for the definition list.
 */
function display(field: MetadataFieldDefinition, stored: unknown) {
  if (stored === undefined || stored === null) return "–";
  const one = (v: unknown) => (Number(field.type) === FieldType.Boolean ? (v === true ? t("typed.yes") : t("typed.no")) : String(v));
  return Array.isArray(stored) ? stored.map(one).join(", ") : one(stored);
}
