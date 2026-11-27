import { useQuery } from "@tanstack/react-query";
import type { ZirrusClient } from "@zirrus/api-client";
import { ApiError } from "./api";
import { Explorer } from "./explorer/Explorer";
import { t } from "./i18n";

/** Query key of the signed-in user; a 401 anywhere resets it to null (signed out). */
export const meKey = ["me"] as const;

/** Signed-in shell: header with the user and sign-out, then the explorer. Anonymous visitors get a sign-in link. */
export function App({ client }: { client: ZirrusClient }) {
  const me = useQuery({
    queryKey: meKey,
    queryFn: async () => {
      const { data, response } = await client.GET("/api/v1/me");
      if (response.status === 401) return null;
      if (data === undefined) throw new ApiError(response.status, "Could not load the current user");
      return data;
    },
  });

  return (
    <>
      <header className="top">
        <h1>{t("app.title")}</h1>
        {me.isSuccess && me.data !== null && (
          <div className="user">
            <span>
              {t("app.signedInAs", { name: me.data.name || me.data.subject })}
              {me.data.isOrganizationAdmin && ` (${t("app.orgAdmin")})`}
            </span>
            <SignOut client={client} />
          </div>
        )}
      </header>
      <main>
        {me.isPending && <p role="status">{t("app.loading")}</p>}
        {me.isError && <p role="alert">{t("app.unreachable")}</p>}
        {me.isSuccess && me.data === null && (
          <p>
            <a href="/login?returnUrl=/">{t("app.signIn")}</a>
          </p>
        )}
        {me.isSuccess && me.data !== null && <Explorer client={client} />}
      </main>
    </>
  );
}

/** Sign-out is a form POST with the CSRF token, so the API can end the identity-provider session too. */
function SignOut({ client }: { client: ZirrusClient }) {
  const token = useQuery({
    queryKey: ["csrf"],
    queryFn: async () => {
      const { data, response } = await client.GET("/api/v1/antiforgery");
      if (data === undefined) throw new ApiError(response.status, "Could not load the CSRF token");
      return data;
    },
  });

  if (!token.isSuccess) return null;
  return (
    <form method="post" action="/logout">
      <input type="hidden" name={token.data.formFieldName} value={token.data.token} />
      <button type="submit">{t("app.signOut")}</button>
    </form>
  );
}
