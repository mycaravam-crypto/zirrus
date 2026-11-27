// UI strings by key (W6: English first, German prepared). Keys are stable; add a language by adding
// a dictionary with the same keys. Placeholders use {name}.
const en = {
  "app.title": "ZIRRUS",
  "app.loading": "Loading…",
  "app.unreachable": "Could not reach the server.",
  "app.signIn": "Sign in",
  "app.signOut": "Sign out",
  "app.signedInAs": "Signed in as {name}",
  "app.orgAdmin": "organization administrator",
  "app.sessionExpired": "Your session has ended. Sign in again to continue.",
  "catalogs.heading": "Catalogs",
  "catalogs.empty": "No catalogs yet.",
  "catalogs.failed": "Could not load catalogs.",
  "catalogs.access.admin": "admin",
  "catalogs.access.editor": "editor",
  "catalogs.access.viewer": "viewer",
  "catalogs.choose": "Choose a catalog to see its assets.",
  "assets.heading": "Assets in {catalog}",
  "assets.empty": "No assets yet.",
  "assets.failed": "Could not load assets.",
  "assets.more": "Load more",
  "assets.openPrefix": "Open",
  "state.uploading": "Uploading",
  "state.quarantined": "Waiting for scan",
  "state.processing": "Processing",
  "state.active": "Active",
  "state.rejected": "Rejected",
  "state.failed": "Failed",
  "upload.heading": "Upload",
  "upload.choose": "Choose images (JPEG, PNG, WebP; up to 100 MiB each)",
  "upload.progress": "Uploading {name}: {percent}%",
  "upload.done": "{name} uploaded; scanning and processing start now.",
  "upload.duplicate": "{name} matches {count} existing asset(s) in this catalog.",
  "upload.failed": "{name} was not uploaded: {reason}",
  "detail.heading": "{name}",
  "detail.close": "Close details",
  "detail.preview": "Preview of {name}",
  "detail.noPreview": "The preview appears when processing has finished.",
  "detail.state": "State",
  "detail.reason": "Reason",
  "detail.type": "Type",
  "detail.size": "Size",
  "detail.checksum": "SHA-256",
  "detail.uploaded": "Uploaded",
  "detail.download": "Download original",
  "detail.retry": "Retry processing",
  "detail.retryFailed": "Retry failed: {reason}",
  "detail.failed": "Could not load the asset.",
  "metadata.heading": "Metadata",
  "metadata.key": "Field",
  "metadata.value": "Value",
  "metadata.add": "Add field",
  "metadata.remove": "Remove field {key}",
  "metadata.removeShort": "Remove",
  "metadata.save": "Save metadata",
  "metadata.saved": "Metadata saved.",
  "metadata.readOnly": "You can view but not change this asset.",
  "metadata.invalidKey": "Field names start with a lowercase letter and use lowercase letters, digits or _ (up to 64).",
  "metadata.duplicateKey": "Field {key} appears twice.",
  "metadata.conflict": "Someone else changed this asset since you opened it. Your changes were not saved.",
  "metadata.reload": "Load the latest version",
  "metadata.failed": "Metadata was not saved: {reason}",
  "metadata.structured": "Structured value (edited in a later version)",
} as const;

export type MessageKey = keyof typeof en;

const de: Record<MessageKey, string> = {
  "app.title": "ZIRRUS",
  "app.loading": "Wird geladen…",
  "app.unreachable": "Der Server ist nicht erreichbar.",
  "app.signIn": "Anmelden",
  "app.signOut": "Abmelden",
  "app.signedInAs": "Angemeldet als {name}",
  "app.orgAdmin": "Organisationsadministration",
  "app.sessionExpired": "Ihre Sitzung ist abgelaufen. Bitte melden Sie sich erneut an.",
  "catalogs.heading": "Kataloge",
  "catalogs.empty": "Noch keine Kataloge.",
  "catalogs.failed": "Kataloge konnten nicht geladen werden.",
  "catalogs.access.admin": "Administration",
  "catalogs.access.editor": "Bearbeitung",
  "catalogs.access.viewer": "Ansicht",
  "catalogs.choose": "Wählen Sie einen Katalog, um seine Assets zu sehen.",
  "assets.heading": "Assets in {catalog}",
  "assets.empty": "Noch keine Assets.",
  "assets.failed": "Assets konnten nicht geladen werden.",
  "assets.more": "Mehr laden",
  "assets.openPrefix": "Öffnen:",
  "state.uploading": "Wird hochgeladen",
  "state.quarantined": "Wartet auf Prüfung",
  "state.processing": "Wird verarbeitet",
  "state.active": "Aktiv",
  "state.rejected": "Abgelehnt",
  "state.failed": "Fehlgeschlagen",
  "upload.heading": "Hochladen",
  "upload.choose": "Bilder wählen (JPEG, PNG, WebP; je bis 100 MiB)",
  "upload.progress": "{name} wird hochgeladen: {percent} %",
  "upload.done": "{name} hochgeladen; Prüfung und Verarbeitung beginnen jetzt.",
  "upload.duplicate": "{name} entspricht {count} vorhandenen Asset(s) in diesem Katalog.",
  "upload.failed": "{name} wurde nicht hochgeladen: {reason}",
  "detail.heading": "{name}",
  "detail.close": "Details schließen",
  "detail.preview": "Vorschau von {name}",
  "detail.noPreview": "Die Vorschau erscheint nach der Verarbeitung.",
  "detail.state": "Status",
  "detail.reason": "Grund",
  "detail.type": "Typ",
  "detail.size": "Größe",
  "detail.checksum": "SHA-256",
  "detail.uploaded": "Hochgeladen",
  "detail.download": "Original herunterladen",
  "detail.retry": "Verarbeitung wiederholen",
  "detail.retryFailed": "Wiederholung fehlgeschlagen: {reason}",
  "detail.failed": "Das Asset konnte nicht geladen werden.",
  "metadata.heading": "Metadaten",
  "metadata.key": "Feld",
  "metadata.value": "Wert",
  "metadata.add": "Feld hinzufügen",
  "metadata.remove": "Feld {key} entfernen",
  "metadata.removeShort": "Entfernen",
  "metadata.save": "Metadaten speichern",
  "metadata.saved": "Metadaten gespeichert.",
  "metadata.readOnly": "Sie können dieses Asset ansehen, aber nicht ändern.",
  "metadata.invalidKey": "Feldnamen beginnen mit einem Kleinbuchstaben und enthalten Kleinbuchstaben, Ziffern oder _ (bis 64).",
  "metadata.duplicateKey": "Das Feld {key} kommt doppelt vor.",
  "metadata.conflict": "Jemand anderes hat dieses Asset inzwischen geändert. Ihre Änderungen wurden nicht gespeichert.",
  "metadata.reload": "Neueste Fassung laden",
  "metadata.failed": "Metadaten wurden nicht gespeichert: {reason}",
  "metadata.structured": "Strukturierter Wert (in einer späteren Version bearbeitbar)",
};

const dictionaries = { en, de } as const;
export type Language = keyof typeof dictionaries;

/** Picks German for German browsers, English otherwise. */
export function detectLanguage(languages: readonly string[] = typeof navigator === "undefined" ? [] : navigator.languages): Language {
  return languages.some((l) => l.toLowerCase().startsWith("de")) ? "de" : "en";
}

let current: Language = "en";

/** Sets the UI language (called once at startup). */
export function setLanguage(language: Language) {
  current = language;
}

/** Translates a key, filling {placeholders}. */
export function t(key: MessageKey, values: Record<string, string | number> = {}): string {
  const template: string = dictionaries[current][key];
  return template.replace(/\{(\w+)\}/g, (_, name: string) => String(values[name] ?? `{${name}}`));
}
