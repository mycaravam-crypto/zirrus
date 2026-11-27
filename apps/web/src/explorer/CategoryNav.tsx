import { Alert, Badge, Button, Collapse, NavLink, Select, Skeleton, Stack, Text, TextInput, Title } from "@mantine/core";
import { IconAlertCircle, IconCategory, IconLayoutGrid, IconPlus } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { CategoryDetails, ZirrusClient } from "@zirrus/api-client";
import { useId, useState, type ReactNode } from "react";
import { ApiError, csrfToken, problemTitle } from "../api";
import { t } from "../i18n";
import { categoriesKey, facetsKey } from "./keys";

/**
 * A catalog's category tree for filtering the grid. Counts come from the facets endpoint for the
 * current search, so they are scoped exactly like the results. Administrators can add categories.
 * @param client API client.
 * @param catalogId Catalog whose categories are shown.
 * @param search Current search term ("" for none); the counts follow it.
 * @param selected Category used as a filter, or null for all assets.
 * @param onSelect Called with a category ID, or null for all assets.
 * @param canAdminister Whether the user may create categories (organization administrators).
 */
export function CategoryNav({
  client,
  catalogId,
  search,
  selected,
  onSelect,
  canAdminister,
}: {
  client: ZirrusClient;
  catalogId: string;
  search: string;
  selected: string | null;
  onSelect: (categoryId: string | null) => void;
  canAdminister: boolean;
}) {
  const categories = useCategories(client, catalogId);
  const facets = useQuery({
    queryKey: facetsKey(catalogId, search),
    queryFn: async () => {
      const { data, response } = await client.GET("/api/v1/catalogs/{catalogId}/assets/facets", {
        params: { path: { catalogId }, query: { q: search || undefined } },
      });
      if (data === undefined) throw new ApiError(response.status, "Could not load counts");
      return data;
    },
  });
  const counts = new Map((facets.data?.categories ?? []).map((f) => [f.categoryId, Number(f.count)]));

  const renderLevel = (parentId: string | null, depth: number): ReactNode =>
    (categories.data ?? [])
      .filter((c) => (c.parentId ?? null) === parentId)
      .map((c) => {
        const children = renderLevel(c.id, depth + 1);
        const count = counts.get(c.id) ?? 0;
        return (
          <li key={c.id}>
            <NavLink
              component="button"
              type="button"
              label={c.name}
              active={c.id === selected}
              aria-current={c.id === selected ? "true" : undefined}
              variant="light"
              className="category-link"
              style={{ paddingLeft: 8 + depth * 14 }}
              leftSection={<IconCategory size={16} stroke={1.6} aria-hidden="true" />}
              rightSection={
                facets.isSuccess ? (
                  <CountBadge count={count} />
                ) : undefined
              }
              onClick={() => onSelect(c.id === selected ? null : c.id)}
            />
            {Array.isArray(children) && children.length > 0 && <ul className="plain-list">{children}</ul>}
          </li>
        );
      });

  return (
    <Stack gap={4} mt="lg">
      <Title order={2} size="xs" tt="uppercase" c="dimmed" fw={700} mb={4} className="nav-heading" id="categories-heading">
        {t("categories.heading")}
      </Title>
      {categories.isPending && <Skeleton h={32} />}
      {categories.isError && (
        <Alert color="red.9" variant="light" icon={<IconAlertCircle />} p="xs">
          {t("categories.failed")}
        </Alert>
      )}
      {categories.isSuccess && (
        <nav aria-labelledby="categories-heading">
          <ul className="plain-list">
            <li>
              <NavLink
                component="button"
                type="button"
                label={t("categories.all")}
                active={selected === null}
                aria-current={selected === null ? "true" : undefined}
                variant="light"
                className="category-link"
                leftSection={<IconLayoutGrid size={16} stroke={1.6} aria-hidden="true" />}
                rightSection={
                  facets.isSuccess ? (
                    <CountBadge count={Number(facets.data.total)} />
                  ) : undefined
                }
                onClick={() => onSelect(null)}
              />
            </li>
            {renderLevel(null, 0)}
          </ul>
          {categories.data.length === 0 && (
            <Text size="sm" c="dimmed" px={8}>
              {t("categories.empty")}
            </Text>
          )}
        </nav>
      )}
      {canAdminister && categories.isSuccess && <NewCategory client={client} catalogId={catalogId} categories={categories.data} />}
    </Stack>
  );
}

/**
 * Number of matching assets: the digit is shown, the words are for screen readers.
 * @param count Matching assets.
 */
function CountBadge({ count }: { count: number }) {
  return (
    <Badge size="xs" variant="default">
      <span aria-hidden="true">{count}</span>
      <span className="sr-only">{t("categories.count", { count })}</span>
    </Badge>
  );
}

/**
 * A catalog's categories, cached per catalog.
 * @param client API client.
 * @param catalogId Catalog.
 * @returns The query.
 */
export function useCategories(client: ZirrusClient, catalogId: string) {
  return useQuery({
    queryKey: categoriesKey(catalogId),
    queryFn: async () => {
      const { data, response } = await client.GET("/api/v1/catalogs/{catalogId}/categories", { params: { path: { catalogId } } });
      if (data === undefined) throw new ApiError(response.status, "Could not load categories");
      return data;
    },
  });
}

/**
 * Category path for display, e.g. "Events / 2026".
 * @param category Category.
 * @param all Every category of the catalog.
 * @returns The path from the root.
 */
export function categoryPath(category: CategoryDetails, all: readonly CategoryDetails[]) {
  const names = [category.name];
  let parent = all.find((c) => c.id === category.parentId);
  while (parent !== undefined && names.length < 20) {
    names.unshift(parent.name);
    const parentId = parent.parentId;
    parent = all.find((c) => c.id === parentId);
  }
  return names.join(" / ");
}

/**
 * Inline form for administrators to add a root or child category.
 * @param client API client.
 * @param catalogId Catalog.
 * @param categories Existing categories, offered as parents.
 */
function NewCategory({ client, catalogId, categories }: { client: ZirrusClient; catalogId: string; categories: readonly CategoryDetails[] }) {
  const queryClient = useQueryClient();
  const formId = useId();
  const [open, setOpen] = useState(false);
  const [name, setName] = useState("");
  const [parentId, setParentId] = useState<string | null>(null);
  const create = useMutation({
    mutationFn: async () => {
      const token = await csrfToken(client);
      const { data, error, response } = await client.POST("/api/v1/catalogs/{catalogId}/categories", {
        params: { path: { catalogId } },
        headers: { "X-CSRF-TOKEN": token },
        body: { name: name.trim(), parentId },
      });
      if (data === undefined) throw new ApiError(response.status, problemTitle(error, `HTTP ${response.status}`));
      return data;
    },
    onSuccess: async () => {
      setName("");
      setParentId(null);
      await queryClient.invalidateQueries({ queryKey: categoriesKey(catalogId) });
    },
  });

  return (
    <div>
      <Button
        variant="subtle"
        size="xs"
        leftSection={<IconPlus size={14} />}
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        aria-controls={formId}
      >
        {t("categories.new")}
      </Button>
      <Collapse expanded={open}>
        <form
          id={formId}
          onSubmit={(event) => {
            event.preventDefault();
            if (name.trim() !== "") create.mutate();
          }}
        >
          <Stack gap="xs" p={8}>
            <TextInput size="xs" label={t("categories.name")} value={name} maxLength={200} onChange={(e) => setName(e.currentTarget.value)} />
            <Select
              size="xs"
              label={t("categories.parent")}
              placeholder={t("categories.noParent")}
              clearable
              data={categories.map((c) => ({ value: c.id, label: categoryPath(c, categories) }))}
              value={parentId}
              onChange={setParentId}
              comboboxProps={{ withinPortal: false }}
            />
            <Button type="submit" size="xs" loading={create.isPending} disabled={name.trim() === ""}>
              {t("categories.create")}
            </Button>
            {create.isSuccess && (
              <Text size="xs" role="status">
                {t("categories.created", { name: create.data.name })}
              </Text>
            )}
            {create.isError && (
              <Text size="xs" role="alert" c="red.9">
                {t("categories.createFailed", { reason: create.error.message })}
              </Text>
            )}
          </Stack>
        </form>
      </Collapse>
    </div>
  );
}
