import { MutationCache, QueryCache, QueryClient } from "@tanstack/react-query";
import { ApiRequestError, isTenantDeactivatedError } from "@/lib/api-client";
import { router } from "@/routes";

const TENANT_DEACTIVATED_PATH = "/tenant-deactivated";

/**
 * Once the tenant is switched off, *every* request fails the same way, so this hook fires
 * from many queries/mutations at once. We route from the first occurrence and no-op the
 * rest by guarding on the current location. Navigation goes through the data router
 * instance directly because this runs outside React. The dead token is intentionally NOT
 * cleared here — clearing flips isAuthenticated false and lets ProtectedRoute race us to
 * /login; the terminal page clears it on its "Back to sign in" action.
 *
 * A revoked or expired ACTING session used to be a second terminal state here. It is not
 * any more: the acting token lives in memory beside a session that was never taken away,
 * so the API client simply drops it and the auth context says so (see acting-store).
 */
function handleGlobalError(error: unknown) {
  if (!isTenantDeactivatedError(error)) return;
  if (router.state.location.pathname === TENANT_DEACTIVATED_PATH) return;
  void router.navigate(TENANT_DEACTIVATED_PATH, { replace: true });
}

export const queryClient = new QueryClient({
  queryCache: new QueryCache({ onError: handleGlobalError }),
  mutationCache: new MutationCache({ onError: handleGlobalError }),
  defaultOptions: {
    queries: {
      retry: (failureCount, error) => {
        if (error instanceof ApiRequestError && (error.status === 401 || error.status === 403)) {
          return false;
        }
        return failureCount < 2;
      },
      staleTime: 30_000,
      refetchOnWindowFocus: false,
    },
  },
});
