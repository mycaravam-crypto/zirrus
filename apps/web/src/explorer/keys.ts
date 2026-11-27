/** Query key of a catalog's asset list; uploads and edits invalidate it. */
export const assetsKey = (catalogId: string) => ["assets", catalogId] as const;

/** Query key of one asset; the metadata editor refreshes it after a conflict. */
export const assetKey = (catalogId: string, assetId: string) => ["asset", catalogId, assetId] as const;
