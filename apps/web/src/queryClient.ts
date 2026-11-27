import { MutationCache, QueryCache, QueryClient } from "@tanstack/react-query";
import { ApiError } from "./api";
import { meKey } from "./App";

/**
 * Ends the session in the UI: removes every cached query except the current user and marks the user
 * as signed out, so the shell shows the sign-in link and no data from the ended session.
 * @param client The query client.
 */
export function endSession(client: QueryClient) {
  client.removeQueries({ predicate: (q) => q.queryKey[0] !== meKey[0] });
  client.setQueryData(meKey, null);
}

/**
 * Query client whose failed requests with 401, from queries and mutations alike, end the session in
 * the UI (see {@link endSession}).
 * @param options Set `retry: false` in tests.
 * @param options.retry Whether failed queries are retried.
 * @returns The client.
 */
export function createQueryClient(options: { retry?: boolean } = {}) {
  const onError = (error: unknown) => {
    if (error instanceof ApiError && error.status === 401) endSession(client);
  };
  const client: QueryClient = new QueryClient({
    queryCache: new QueryCache({ onError }),
    mutationCache: new MutationCache({ onError }),
    defaultOptions: {
      queries: {
        retry: options.retry === false ? false : (count, error) => !(error instanceof ApiError && error.status < 500) && count < 2,
      },
    },
  });
  return client;
}
