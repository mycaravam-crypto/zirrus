import { useQuery } from "@tanstack/react-query";
import type { ZirrusClient } from "@zirrus/api-client";

/** Walking-skeleton page: proves SPA → Caddy → API → PostgreSQL by listing catalogs. */
export function App({ client }: { client: ZirrusClient }) {
  const catalogs = useQuery({
    queryKey: ["catalogs"],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/v1/catalogs");
      if (error !== undefined || data === undefined) throw new Error("Could not load catalogs");
      return data;
    },
  });

  return (
    <main>
      <h1>ZIRRUS</h1>
      <h2>Catalogs</h2>
      {catalogs.isPending && <p role="status">Loading…</p>}
      {catalogs.isError && <p role="alert">Could not load catalogs.</p>}
      {catalogs.isSuccess && catalogs.data.length === 0 && <p>No catalogs yet.</p>}
      {catalogs.isSuccess && catalogs.data.length > 0 && (
        <ul>
          {catalogs.data.map((c) => (
            <li key={c.id}>{c.name}</li>
          ))}
        </ul>
      )}
    </main>
  );
}
