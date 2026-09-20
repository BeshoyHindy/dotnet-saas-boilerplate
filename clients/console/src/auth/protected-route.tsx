import { Navigate, Outlet, useLocation } from "react-router-dom";
import { useAuth } from "@/auth/use-auth";
import { isOperator, NotAnOperatorView } from "@/auth/operator-gate";

export function ProtectedRoute() {
  const { isAuthenticated, isInitializing, permissionsHydrated, user } = useAuth();
  const location = useLocation();

  // Resolving a stored session (silent token refresh) — hold rendering so we
  // neither flash the app with a stale/expired token nor bounce to
  // /login before the refresh has had a chance to restore the session.
  if (isInitializing) {
    return (
      <div
        className="flex min-h-screen items-center justify-center text-sm text-muted-foreground"
        role="status"
        aria-busy="true"
      >
        <span className="sr-only">Restoring your session…</span>
        <span
          className="size-5 animate-spin rounded-full border-2 border-current border-t-transparent"
          aria-hidden
        />
      </div>
    );
  }

  if (!isAuthenticated) {
    return <Navigate to="/login" replace state={{ from: location }} />;
  }

  // A tenant user signing in here holds a valid session and no operator permission, so
  // every screen behind this point would 403. Tell them once, in one place, rather than
  // rendering a shell of failing panels (ADR-0008). Waits for hydration so a warm reload
  // does not flash this at an operator whose permissions are still in flight.
  if (permissionsHydrated && !isOperator(user?.permissions ?? [])) {
    return <NotAnOperatorView />;
  }

  return <Outlet />;
}
