import type { UploadResult, ZirrusClient } from "@zirrus/api-client";

/** An API failure with its HTTP status; 401 means the session ended. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message);
  }
}

/** Problem details title, or a generic message. */
export function problemTitle(body: unknown, fallback: string): string {
  return typeof body === "object" && body !== null && "title" in body && typeof body.title === "string" ? body.title : fallback;
}

/** Fetches the antiforgery token sent with every unsafe request (X-CSRF-TOKEN). */
export async function csrfToken(client: ZirrusClient): Promise<string> {
  const { data, response } = await client.GET("/api/v1/antiforgery");
  if (data === undefined) throw new ApiError(response.status, "Could not load the CSRF token");
  return data.token;
}

/** Base path of an asset's endpoints. */
export const assetPath = (catalogId: string, assetId: string) => `/api/v1/catalogs/${catalogId}/assets/${assetId}`;

/**
 * Uploads one file with progress (fetch has no upload progress, so this uses XMLHttpRequest).
 * Cookies are sent automatically on the same origin; the CSRF token goes in the header.
 */
export function uploadFile(
  catalogId: string,
  file: File,
  token: string,
  onProgress: (percent: number) => void,
): Promise<UploadResult> {
  return new Promise((resolve, reject) => {
    const request = new XMLHttpRequest();
    request.open("POST", `/api/v1/catalogs/${catalogId}/assets`);
    request.setRequestHeader("X-CSRF-TOKEN", token);
    request.responseType = "json";
    request.upload.onprogress = (event) => {
      if (event.lengthComputable) onProgress(Math.round((event.loaded / event.total) * 100));
    };
    request.onload = () => {
      if (request.status === 201) {
        resolve(request.response as UploadResult);
      } else {
        reject(new ApiError(request.status, problemTitle(request.response, `HTTP ${request.status}`)));
      }
    };
    request.onerror = () => reject(new ApiError(0, "network error"));
    const form = new FormData();
    form.append("file", file, file.name);
    request.send(form);
  });
}

/** Human-readable byte size. */
export function formatBytes(bytes: number | string | null | undefined): string {
  const value = typeof bytes === "string" ? Number(bytes) : bytes;
  if (value === null || value === undefined || Number.isNaN(value)) return "–";
  const units = ["B", "KiB", "MiB", "GiB"];
  let size = value;
  let unit = 0;
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024;
    unit++;
  }
  return `${size.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`;
}

/** States in which the pipeline is still working; the UI polls while any asset is in one. */
export const pendingStates = new Set(["uploading", "quarantined", "processing"]);
