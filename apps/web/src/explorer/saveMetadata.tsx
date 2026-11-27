import { Alert, Badge, Button, Group, Stack, Text, Title } from "@mantine/core";
import { IconAlertCircle, IconAlertTriangle, IconCircleCheck } from "@tabler/icons-react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import type { AssetDetails, ZirrusClient } from "@zirrus/api-client";
import { useState, type ReactNode } from "react";
import { ApiError, csrfToken, problemTitle } from "../api";
import { t } from "../i18n";
import { assetKey, assetsKey } from "./keys";

/**
 * Saves an asset's metadata with If-Match on the revision the form was loaded from. A 412 means
 * someone else saved first: the caller shows the conflict and the user reloads instead of overwriting.
 * @param client API client.
 * @param catalogId Catalog of the asset.
 * @param asset Asset at the revision being edited.
 * @param onSaved Called with the new revision after a successful save.
 * @returns The mutation (call `mutate(body)`) and whether the last failure was a conflict.
 */
export function useSaveMetadata(client: ZirrusClient, catalogId: string, asset: AssetDetails, onSaved: (revision: number) => void) {
  const queryClient = useQueryClient();
  const [conflict, setConflict] = useState(false);
  const save = useMutation({
    mutationFn: async (body: Record<string, unknown>) => {
      const token = await csrfToken(client);
      const { data, error, response } = await client.PUT("/api/v1/catalogs/{catalogId}/assets/{assetId}/metadata", {
        params: { path: { catalogId, assetId: asset.id }, header: { "If-Match": `"${asset.revision}"` } },
        headers: { "X-CSRF-TOKEN": token },
        body,
      });
      if (data === undefined) throw new ApiError(response.status, problemTitle(error, `HTTP ${response.status}`));
      return data;
    },
    onSuccess: async (data) => {
      onSaved(Number(data.revision));
      queryClient.setQueryData(assetKey(catalogId, asset.id), data);
      await queryClient.invalidateQueries({ queryKey: assetsKey(catalogId) }); // search results may change
    },
    onError: (error) => setConflict(error instanceof ApiError && error.status === 412),
  });
  return { save, conflict };
}

/**
 * Saved, conflict (412) and failure messages below a metadata form.
 * @param saved Whether this revision is the one the user just saved.
 * @param conflict Whether the last save failed with 412.
 * @param error The last save error, if any.
 * @param onReload Reloads the latest revision after a conflict.
 */
export function SaveFeedback({ saved, conflict, error, onReload }: { saved: boolean; conflict: boolean; error: Error | null; onReload: () => void }) {
  return (
    <>
      {saved && (
        <Group gap={6} role="status" className="saved-note">
          <IconCircleCheck size={16} aria-hidden="true" />
          <Text size="sm">{t("metadata.saved")}</Text>
        </Group>
      )}
      {conflict && (
        <Alert color="orange.9" variant="light" icon={<IconAlertTriangle />} title={t("metadata.conflictTitle")}>
          <Stack gap="xs" align="flex-start">
            <Text size="sm">{t("metadata.conflict")}</Text>
            <Button size="xs" variant="default" onClick={onReload}>
              {t("metadata.reload")}
            </Button>
          </Stack>
        </Alert>
      )}
      {error !== null && !conflict && (
        <Alert color="red.9" variant="light" icon={<IconAlertCircle />} p="xs">
          <Text size="sm">{t("metadata.failed", { reason: error.message })}</Text>
        </Alert>
      )}
    </>
  );
}

/**
 * Heading row of the metadata section: title and the revision being edited.
 * @param id Element ID the section is labelled by.
 * @param revision Asset revision.
 * @param children Optional extra content (e.g. the schema version).
 */
export function MetadataHeading({ id, revision, children }: { id: string; revision: number | string; children?: ReactNode }) {
  return (
    <Group justify="space-between" align="center">
      <Title order={3} size="h5" id={id}>
        {t("metadata.heading")}
      </Title>
      <Group gap={6}>
        {children}
        <Badge variant="default" size="sm" radius="sm">
          {t("metadata.revision", { revision: String(revision) })}
        </Badge>
      </Group>
    </Group>
  );
}
