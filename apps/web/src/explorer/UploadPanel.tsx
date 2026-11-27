import { useQueryClient } from "@tanstack/react-query";
import type { ZirrusClient } from "@zirrus/api-client";
import { useId, useState } from "react";
import { ApiError, csrfToken, uploadFile } from "../api";
import { t } from "../i18n";
import { assetsKey } from "./keys";

type Status = { name: string; message: string; kind: "progress" | "done" | "error" };

/** Uploads chosen files one after another with progress; editors only. */
export function UploadPanel({ client, catalogId }: { client: ZirrusClient; catalogId: string }) {
  const queryClient = useQueryClient();
  const inputId = useId();
  const [statuses, setStatuses] = useState<Status[]>([]);
  const [busy, setBusy] = useState(false);

  async function upload(files: File[]) {
    if (files.length === 0) return;
    setBusy(true);
    const update = (name: string, status: Omit<Status, "name">) =>
      setStatuses((current) => [...current.filter((s) => s.name !== name), { name, ...status }]);
    try {
      const token = await csrfToken(client);
      for (const file of files) {
        update(file.name, { kind: "progress", message: t("upload.progress", { name: file.name, percent: 0 }) });
        try {
          const result = await uploadFile(catalogId, file, token, (percent) =>
            update(file.name, { kind: "progress", message: t("upload.progress", { name: file.name, percent }) }),
          );
          const duplicates = result.duplicates.length;
          update(file.name, {
            kind: "done",
            message: duplicates > 0 ? `${t("upload.done", { name: file.name })} ${t("upload.duplicate", { name: file.name, count: duplicates })}` : t("upload.done", { name: file.name }),
          });
        } catch (error) {
          const reason = error instanceof ApiError ? error.message : String(error);
          update(file.name, { kind: "error", message: t("upload.failed", { name: file.name, reason }) });
        }
        await queryClient.invalidateQueries({ queryKey: assetsKey(catalogId) });
      }
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="upload" aria-labelledby={`${inputId}-heading`}>
      <h3 id={`${inputId}-heading`}>{t("upload.heading")}</h3>
      <label htmlFor={inputId}>{t("upload.choose")}</label>
      <input
        id={inputId}
        type="file"
        multiple
        accept="image/jpeg,image/png,image/webp"
        disabled={busy}
        onChange={(event) => {
          const files = Array.from(event.currentTarget.files ?? []); // copy before clearing the input
          event.currentTarget.value = "";
          void upload(files);
        }}
      />
      <ul aria-live="polite" className="upload-status">
        {statuses.map((s) => (
          <li key={s.name} className={s.kind} role={s.kind === "error" ? "alert" : undefined}>
            {s.message}
          </li>
        ))}
      </ul>
    </section>
  );
}
