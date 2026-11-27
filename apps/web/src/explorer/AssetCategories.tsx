import { Badge, CloseButton, Group, Select, Stack, Text, Title } from "@mantine/core";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { ZirrusClient } from "@zirrus/api-client";
import { useId } from "react";
import { ApiError, csrfToken, problemTitle } from "../api";
import { t } from "../i18n";
import { categoryPath, useCategories } from "./CategoryNav";
import { assetCategoriesKey, assetsKey } from "./keys";

/**
 * The categories an asset is in. Editors add it to a category or remove it from one; viewers only
 * see the list. Assignment needs an active asset, as the API requires.
 * @param client API client.
 * @param catalogId Catalog of the asset.
 * @param assetId Asset.
 * @param active Whether the asset is active (assignable).
 * @param canEdit Whether the user may change assignments.
 */
export function AssetCategories({
  client,
  catalogId,
  assetId,
  active,
  canEdit,
}: {
  client: ZirrusClient;
  catalogId: string;
  assetId: string;
  active: boolean;
  canEdit: boolean;
}) {
  const queryClient = useQueryClient();
  const headingId = useId();
  const all = useCategories(client, catalogId);
  const assigned = useQuery({
    queryKey: assetCategoriesKey(catalogId, assetId),
    queryFn: async () => {
      const { data, response } = await client.GET("/api/v1/catalogs/{catalogId}/assets/{assetId}/categories", {
        params: { path: { catalogId, assetId } },
      });
      if (data === undefined) throw new ApiError(response.status, "Could not load the asset's categories");
      return data;
    },
  });

  const change = useMutation({
    mutationFn: async ({ categoryId, add }: { categoryId: string; add: boolean }) => {
      const token = await csrfToken(client);
      const options = { params: { path: { catalogId, assetId, categoryId } }, headers: { "X-CSRF-TOKEN": token } };
      const { data, error, response } = add
        ? await client.PUT("/api/v1/catalogs/{catalogId}/assets/{assetId}/categories/{categoryId}", options)
        : await client.DELETE("/api/v1/catalogs/{catalogId}/assets/{assetId}/categories/{categoryId}", options);
      if (data === undefined) throw new ApiError(response.status, problemTitle(error, `HTTP ${response.status}`));
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: assetCategoriesKey(catalogId, assetId) });
      await queryClient.invalidateQueries({ queryKey: ["facets", catalogId] });
      await queryClient.invalidateQueries({ queryKey: assetsKey(catalogId) });
    },
  });

  const categories = all.data ?? [];
  const current = assigned.data ?? [];
  const available = categories.filter((c) => !current.some((a) => a.id === c.id));

  return (
    <Stack component="section" aria-labelledby={headingId} gap="xs">
      <Title order={3} size="h5" id={headingId}>
        {t("assetCategories.heading")}
      </Title>
      {assigned.isSuccess && current.length === 0 && (
        <Text size="sm" c="dimmed">
          {t("assetCategories.none")}
        </Text>
      )}
      {current.length > 0 && (
        <Group gap={6} component="ul" className="plain-list">
          {current.map((c) => {
            const label = categoryPath(c, categories);
            return (
              <li key={c.id}>
                <Badge
                  variant="light"
                  size="lg"
                  radius="sm"
                  tt="none"
                  rightSection={
                    canEdit ? (
                      <CloseButton
                        size="xs"
                        aria-label={t("assetCategories.remove", { name: label })}
                        onClick={() => change.mutate({ categoryId: c.id, add: false })}
                        disabled={change.isPending}
                      />
                    ) : undefined
                  }
                >
                  {label}
                </Badge>
              </li>
            );
          })}
        </Group>
      )}
      {canEdit && active && available.length > 0 && (
        <Select
          label={t("assetCategories.add")}
          placeholder={t("assetCategories.choose")}
          size="sm"
          searchable
          data={available.map((c) => ({ value: c.id, label: categoryPath(c, categories) }))}
          value={null}
          onChange={(value) => value !== null && change.mutate({ categoryId: value, add: true })}
          disabled={change.isPending}
          comboboxProps={{ withinPortal: false }}
        />
      )}
      {canEdit && !active && (
        <Text size="sm" c="dimmed">
          {t("assetCategories.activeOnly")}
        </Text>
      )}
      {change.isError && (
        <Text size="sm" role="alert" c="red.9">
          {t("assetCategories.failed", { reason: change.error.message })}
        </Text>
      )}
    </Stack>
  );
}
