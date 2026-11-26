import { useQuery } from "@tanstack/react-query";
import type { ZirrusClient } from "@zirrus/api-client";

/** Signed-in shell: who am I, my catalogs, sign out. Anonymous visitors get a sign-in link. */
export function App({ client }: { client: ZirrusClient }) {
  const me = useQuery({
    queryKey: ["me"],
    queryFn: async () => {
      const { data, response } = await client.GET("/api/v1/me");
      if (response.status === 401) return null;
      if (data === undefined) throw new Error("Could not load the current user");
      return data;
    },
  });

  return (
    <main>
      <h1>ZIRRUS</h1>
      {me.isPending && <p role="status">Loading…</p>}
      {me.isError && <p role="alert">Could not reach the server.</p>}
      {me.isSuccess && me.data === null && (
        <p>
          <a href="/login?returnUrl=/">Sign in</a>
        </p>
      )}
      {me.isSuccess && me.data !== null && (
        <>
          <p>
            Signed in as <strong>{me.data.name || me.data.subject}</strong>
            {me.data.isOrganizationAdmin && " (organization administrator)"}
          </p>
          <SignOut client={client} />
          <Catalogs client={client} />
        </>
      )}
    </main>
  );
}

function Catalogs({ client }: { client: ZirrusClient }) {
  const catalogs = useQuery({
    queryKey: ["catalogs"],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/v1/catalogs");
      if (error !== undefined || data === undefined) throw new Error("Could not load catalogs");
      return data;
    },
  });

  return (
    <section aria-labelledby="catalogs-heading">
      <h2 id="catalogs-heading">Catalogs</h2>
      {catalogs.isPending && <p role="status">Loading…</p>}
      {catalogs.isError && <p role="alert">Could not load catalogs.</p>}
      {catalogs.isSuccess && catalogs.data.length === 0 && <p>No catalogs yet.</p>}
      {catalogs.isSuccess && catalogs.data.length > 0 && (
        <ul>
          {catalogs.data.map((c) => (
            <li key={c.id}>
              {c.name} <span>({c.access})</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

/** Sign-out is a form POST with the CSRF token, so the API can end the identity-provider session too. */
function SignOut({ client }: { client: ZirrusClient }) {
  const token = useQuery({
    queryKey: ["csrf"],
    queryFn: async () => {
      const { data } = await client.GET("/api/v1/antiforgery");
      if (data === undefined) throw new Error("Could not load the CSRF token");
      return data;
    },
  });

  if (!token.isSuccess) return null;
  return (
    <form method="post" action="/logout">
      <input type="hidden" name={token.data.formFieldName} value={token.data.token} />
      <button type="submit">Sign out</button>
    </form>
  );
}
