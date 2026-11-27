import { ActionIcon, Alert, Badge, Button, Center, CloseButton, Code, CopyButton, Divider, Group, Image, Loader, Paper, Stack, Text, Title, Tooltip } from "@mantine/core";
import { IconAlertCircle, IconCheck, IconCopy, IconDownload, IconHourglassHigh, IconRefresh } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { AssetDetails, ZirrusClient } from "@zirrus/api-client";
import { useEffect, useId, useRef } from "react";
import { ApiError, assetPath, csrfToken, formatBytes, pendingStates, problemTitle } from "../api";
import { t } from "../i18n";
import { stateColor, stateLabel } from "./AssetGrid";
import { assetKey, assetsKey } from "./keys";
import { AssetCategories } from "./AssetCategories";
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
    <Paper component="aside" className="detail" aria-labelledby={headingId} radius="lg" p="lg" shadow="xs">
      {asset.isPending && (
        <Center mih={200}>
          <Loader aria-hidden="true" />
          <Text role="status" className="sr-only">
            {t("app.loading")}
          </Text>
        </Center>
      )}
      {asset.isError && (
        <Group justify="space-between" align="flex-start" wrap="nowrap">
          <Alert color="red.9" variant="light" icon={<IconAlertCircle />}>
            {t("detail.failed")}
          </Alert>
          <CloseButton onClick={onClose} aria-label={t("detail.close")} />
        </Group>
      )}
      {asset.isSuccess && (
        <Stack gap="md">
          <Group justify="space-between" align="flex-start" wrap="nowrap">
            <Title order={2} size="h4" id={headingId} ref={heading} tabIndex={-1} className="detail-title">
              {t("detail.heading", { name: asset.data.fileName })}
            </Title>
            <CloseButton onClick={onClose} aria-label={t("detail.close")} size="lg" />
          </Group>

          {asset.data.state === "active" ? (
            <Image
              className="preview"
              radius="md"
              src={`${assetPath(catalogId, assetId)}/renditions/preview`}
              alt={t("detail.preview", { name: asset.data.fileName })}
            />
          ) : (
            <Center className="no-preview">
              <Stack align="center" gap={6}>
                <IconHourglassHigh size={28} stroke={1.5} aria-hidden="true" />
                <Text size="sm" c="dimmed" ta="center">
                  {t("detail.noPreview")}
                </Text>
              </Stack>
            </Center>
          )}

          <dl className="facts">
            <dt>{t("detail.state")}</dt>
            <dd>
              <Badge variant="filled" color={stateColor[asset.data.state] ?? "gray.8"}>
                {stateLabel(asset.data.state)}
              </Badge>
            </dd>
            {asset.data.statusReason && (
              <>
                <dt>{t("detail.reason")}</dt>
                <dd className="reason">{asset.data.statusReason}</dd>
              </>
            )}
            <dt>{t("detail.type")}</dt>
            <dd>{asset.data.mediaType}</dd>
            <dt>{t("detail.size")}</dt>
            <dd>{formatBytes(asset.data.sizeBytes)}</dd>
            <dt>{t("detail.checksum")}</dt>
            <dd>
              {asset.data.sha256 ? (
                <Group gap={4} wrap="nowrap">
                  <Code title={asset.data.sha256}>{shortHash(asset.data.sha256)}</Code>
                  <CopyButton value={asset.data.sha256} timeout={1500}>
                    {({ copied, copy }) => (
                      <Tooltip label={copied ? t("detail.copied") : t("detail.copy")} withArrow position="top">
                        <ActionIcon variant="subtle" color={copied ? "teal.9" : "gray.7"} onClick={copy} aria-label={t("detail.copy")} size="sm">
                          {copied ? <IconCheck size={14} /> : <IconCopy size={14} />}
                        </ActionIcon>
                      </Tooltip>
                    )}
                  </CopyButton>
                </Group>
              ) : (
                "–"
              )}
            </dd>
            <dt>{t("detail.uploaded")}</dt>
            <dd>
              <time dateTime={asset.data.createdAt}>{new Date(asset.data.createdAt).toLocaleString()}</time>
            </dd>
          </dl>

          {(asset.data.state === "active" || (canEdit && (asset.data.state === "failed" || asset.data.state === "quarantined"))) && (
            <Group gap="sm">
              {asset.data.state === "active" && (
                <Button
                  component="a"
                  href={`${assetPath(catalogId, assetId)}/content`}
                  download={asset.data.fileName}
                  leftSection={<IconDownload size={16} />}
                >
                  {t("detail.download")}
                </Button>
              )}
              {canEdit && (asset.data.state === "failed" || asset.data.state === "quarantined") && (
                <Button variant="default" onClick={() => retry.mutate()} loading={retry.isPending} leftSection={<IconRefresh size={16} />}>
                  {t("detail.retry")}
                </Button>
              )}
            </Group>
          )}
          {retry.isError && (
            <Alert color="red.9" variant="light" icon={<IconAlertCircle />}>
              {t("detail.retryFailed", { reason: retry.error.message })}
            </Alert>
          )}
          <Divider />
          <MetadataEditor client={client} catalogId={catalogId} asset={asset.data} canEdit={canEdit} />
          <Divider />
          <AssetCategories client={client} catalogId={catalogId} assetId={assetId} active={asset.data.state === "active"} canEdit={canEdit} />
        </Stack>
      )}
    </Paper>
  );
}

/**
 * Shortened checksum for display; the full value is in the title and copy button.
 * @param hash Hex digest.
 * @returns The first and last eight characters.
 */
function shortHash(hash: string) {
  return hash.length > 20 ? `${hash.slice(0, 8)}…${hash.slice(-8)}` : hash;
}
