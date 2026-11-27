import { QueryCache, QueryClient } from "@tanstack/react-query";
import { ApiError } from "./api";
import { meKey } from "./App";

/**
 * Query client whose failed requests with 401 mark the user as signed out: the shell then shows
 * the sign-in link and no cached data from the ended session.
 */
export function createQueryClient(options: { retry?: boolean } = {}) {
  const client: QueryClient = new QueryClient({
    queryCache: new QueryCache({
      onError: (error) => {
        if (error instanceof ApiError && error.status === 401) {
          client.removeQueries({ predicate: (q) => q.queryKey[0] !== meKey[0] });
          client.setQueryData(meKey, null);
        }
      },
    }),
    defaultOptions: {
      queries: {
        retry: options.retry === false ? false : (count, error) => !(error instanceof ApiError && error.status < 500) && count < 2,
      },
    },
  });
  return client;
}
