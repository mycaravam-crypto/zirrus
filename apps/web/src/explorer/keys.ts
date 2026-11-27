/** Query key of a catalog's asset list; uploads and edits invalidate it. */
export const assetsKey = (catalogId: string) => ["assets", catalogId] as const;

/** Query key of one asset; the metadata editor refreshes it after a conflict. */
export const assetKey = (catalogId: string, assetId: string) => ["asset", catalogId, assetId] as const;

/** Query key of a catalog's category tree. */
export const categoriesKey = (catalogId: string) => ["categories", catalogId] as const;

/** Query key of the categories one asset is assigned to. */
export const assetCategoriesKey = (catalogId: string, assetId: string) => ["asset-categories", catalogId, assetId] as const;

/** Query key of facet counts for a catalog and search term. */
export const facetsKey = (catalogId: string, search: string) => ["facets", catalogId, search] as const;

/** Query key of a catalog's metadata schema. */
export const schemaKey = (catalogId: string) => ["schema", catalogId] as const;
