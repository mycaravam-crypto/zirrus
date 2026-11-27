// Typed client for the ZIRRUS API. `schema.ts` is generated from openapi/zirrus-api.json by
// `pnpm generate`; CI fails when either file is stale (ADR 0011, contract job).
import createClient from "openapi-fetch";
import type { components, paths } from "./schema";

export type CatalogSummary = components["schemas"]["CatalogSummary"];
export type CurrentUserInfo = components["schemas"]["CurrentUserInfo"];
export type CsrfToken = components["schemas"]["CsrfToken"];
export type AssetDetails = components["schemas"]["AssetDetails"];
export type AssetSummary = components["schemas"]["AssetSummary"];
export type AssetPage = components["schemas"]["AssetPage"];
export type UploadResult = components["schemas"]["UploadResult"];
export type AssetFacets = components["schemas"]["AssetFacets"];
export type CategoryFacet = components["schemas"]["CategoryFacet"];
export type CategoryDetails = components["schemas"]["CategoryDetails"];
export type SchemaDetails = components["schemas"]["SchemaDetails"];
export type MetadataFieldDefinition = components["schemas"]["MetadataFieldDefinition"];

/** Creates a client for the same-origin API (Caddy routes /api to the API container). */
export function createZirrusClient(baseUrl = "") {
  return createClient<paths>({ baseUrl, credentials: "same-origin" });
}

export type ZirrusClient = ReturnType<typeof createZirrusClient>;
