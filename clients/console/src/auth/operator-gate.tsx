import { ShieldAlert } from "lucide-react";
import { useAuth } from "@/auth/use-auth";
import { Button } from "@/components/ui/button";
import { env } from "@/env";
import { MultitenancyPermissions, SystemPermissions } from "@/lib/permissions";

/**
 * The two permissions that mean "this person operates the platform". Both are root-tenant
 * only in the server's catalog (`IsRoot`), and between them they cover every screen this
 * app has: the tenant registry and the cross-tenant exchange.
 *
 * Deliberately a *permission* check, not a tenant-name check: "root" is a seeded
 * identifier, not a security boundary, and the server gates on these same strings.
 */
const OPERATOR_PERMISSIONS: readonly string[] = [
  MultitenancyPermissions.Tenants.View,
  SystemPermissions.Platform.CrossTenantImpersonate,
];

export function isOperator(permissions: readonly string[]): boolean {
  return OPERATOR_PERMISSIONS.some((p) => permissions.includes(p));
}

/**
 * OperatorGate — what a tenant user sees if they sign in here (ADR-0008).
 *
 * The console is the operator tool; the tenant app is a different deployment. A tenant
 * user's credentials are perfectly valid, so the sign-in succeeds and we must not pretend
 * otherwise — but every screen behind this point would 403. Say that plainly, once,
 * instead of rendering a shell whose every panel fails.
 *
 * While an operator is ACTING inside a tenant this still passes: `AuthProvider` hydrates
 * permissions as the operator (`AS_OPERATOR`), so the chrome keeps describing the person
 * who signed in, not the subject they are acting as.
 */
export function NotAnOperatorView() {
  const { user, logout } = useAuth();
  const dashboardUrl = env.dashboardUrl;

  return (
    <div className="flex min-h-screen items-center justify-center p-6">
      <div className="card-shell w-full max-w-md px-8 py-9">
        <div className="flex items-center gap-3">
          <span className="grid h-10 w-10 place-items-center rounded-md border border-[var(--color-border)] bg-[var(--color-surface-2)] text-[var(--color-muted-foreground)]">
            <ShieldAlert className="h-5 w-5" />
          </span>
          <div className="meta text-[var(--color-muted-foreground)]">
            // operators only
          </div>
        </div>

        <h2 className="mt-5 font-display text-2xl font-semibold tracking-tight">
          This console is for platform operators.
        </h2>
        <p className="mt-2 text-sm leading-relaxed text-[var(--color-muted-foreground)]">
          You are signed in as{" "}
          <code className="code-chip">{user?.email ?? user?.name ?? "your account"}</code>, and that
          account administers a tenant rather than the platform. Your own workspace lives in the
          app, not here.
        </p>

        <div className="mt-7 flex flex-wrap items-center gap-2">
          {dashboardUrl && (
            <Button asChild size="sm">
              <a href={dashboardUrl}>Go to the app</a>
            </Button>
          )}
          <Button variant="outline" size="sm" onClick={logout}>
            Sign out
          </Button>
        </div>
      </div>
    </div>
  );
}
