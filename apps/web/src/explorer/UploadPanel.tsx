import { Group, Paper, Progress, Stack, Text, ThemeIcon, Title } from "@mantine/core";
import { Dropzone, type FileRejection } from "@mantine/dropzone";
import { IconAlertCircle, IconCircleCheck, IconCloudUpload, IconPhoto, IconUpload, IconX } from "@tabler/icons-react";
import { useQueryClient } from "@tanstack/react-query";
import type { ZirrusClient } from "@zirrus/api-client";
import { useId, useState } from "react";
import { ApiError, csrfToken, uploadFile } from "../api";
import { t } from "../i18n";
import { endSession } from "../queryClient";
import { assetsKey } from "./keys";

type Status = { id: number; name: string; message: string; kind: "progress" | "done" | "error"; percent: number };

const accepted = ["image/jpeg", "image/png", "image/webp"];
const maxBytes = 100 * 1024 * 1024;

/**
 * Drop zone that uploads files one after another with per-file progress; editors only.
 * @param client API client.
 * @param catalogId Catalog that receives the uploads.
 */
export function UploadPanel({ client, catalogId }: { client: ZirrusClient; catalogId: string }) {
  const queryClient = useQueryClient();
  const headingId = useId();
  const [statuses, setStatuses] = useState<Status[]>([]);
  const [busy, setBusy] = useState(false);
  const [nextId, setNextId] = useState(0);

  // Each chosen file gets its own row, so two files with the same name do not overwrite each other.
  const update = (id: number, status: Omit<Status, "id">) =>
    setStatuses((current) => [...current.filter((s) => s.id !== id), { id, ...status }].sort((a, b) => a.id - b.id));

  async function upload(files: File[]) {
    if (files.length === 0) return;
    const base = nextId;
    setNextId(base + files.length);
    setBusy(true);
    try {
      const token = await csrfToken(client);
      for (const [index, file] of files.entries()) {
        const id = base + index;
        update(id, { name: file.name, kind: "progress", percent: 0, message: t("upload.progress", { name: file.name, percent: 0 }) });
        try {
          const result = await uploadFile(catalogId, file, token, (percent) =>
            update(id, { name: file.name, kind: "progress", percent, message: t("upload.progress", { name: file.name, percent }) }),
          );
          const duplicates = result.duplicates.length;
          update(id, {
            name: file.name,
            kind: "done",
            percent: 100,
            message:
              duplicates > 0
                ? `${t("upload.done", { name: file.name })} ${t("upload.duplicate", { name: file.name, count: duplicates })}`
                : t("upload.done", { name: file.name }),
          });
        } catch (error) {
          if (error instanceof ApiError && error.status === 401) {
            endSession(queryClient); // the session ended: back to sign-in, no cached data
            return;
          }

          const reason = error instanceof ApiError ? error.message : String(error);
          update(id, { name: file.name, kind: "error", percent: 0, message: t("upload.failed", { name: file.name, reason }) });
        }
        await queryClient.invalidateQueries({ queryKey: assetsKey(catalogId) });
      }
    } finally {
      setBusy(false);
    }
  }

  function reject(rejections: FileRejection[]) {
    const base = nextId;
    setNextId(base + rejections.length);
    rejections.forEach(({ file }, index) =>
      update(base + index, { name: file.name, kind: "error", percent: 0, message: t("upload.failed", { name: file.name, reason: t("upload.rejected") }) }),
    );
  }

  return (
    <Paper p="md" radius="lg" component="section" aria-labelledby={headingId} className="upload">
      <Title order={3} size="h5" id={headingId} mb="sm">
        {t("upload.heading")}
      </Title>
      <Dropzone
        onDrop={(files) => void upload(files)}
        onReject={reject}
        accept={accepted}
        maxSize={maxBytes}
        loading={busy}
        radius="md"
        className="dropzone"
        inputProps={{ "aria-label": t("upload.choose") }}
      >
        <Group justify="center" gap="lg" mih={110} style={{ pointerEvents: "none" }} wrap="nowrap">
          <Dropzone.Accept>
            <ThemeIcon size={52} radius="xl" variant="light" aria-hidden="true">
              <IconUpload size={28} stroke={1.5} />
            </ThemeIcon>
          </Dropzone.Accept>
          <Dropzone.Reject>
            <ThemeIcon size={52} radius="xl" variant="light" color="red" aria-hidden="true">
              <IconX size={28} stroke={1.5} />
            </ThemeIcon>
          </Dropzone.Reject>
          <Dropzone.Idle>
            <ThemeIcon size={52} radius="xl" variant="light" aria-hidden="true">
              <IconCloudUpload size={28} stroke={1.5} />
            </ThemeIcon>
          </Dropzone.Idle>
          <div>
            <Text fw={600}>{t("upload.drop")}</Text>
            <Text size="sm" c="dimmed" mt={4}>
              {t("upload.hint")}
            </Text>
          </div>
        </Group>
      </Dropzone>
      {/* Status rows are a live region; failures are alerts. */}
      <ul aria-live="polite" className="plain-list upload-status">
        {statuses.map((s) => (
          <li key={s.id} role={s.kind === "error" ? "alert" : undefined}>
            <Group gap="sm" wrap="nowrap" align="flex-start">
              <span className={`upload-icon ${s.kind}`} aria-hidden="true">
                {s.kind === "done" ? <IconCircleCheck size={18} /> : s.kind === "error" ? <IconAlertCircle size={18} /> : <IconPhoto size={18} />}
              </span>
              <Stack gap={4} style={{ flex: 1 }}>
                <Text size="sm">{s.message}</Text>
                {s.kind === "progress" && <Progress value={s.percent} size="sm" animated aria-hidden="true" />}
              </Stack>
            </Group>
          </li>
        ))}
      </ul>
    </Paper>
  );
}
