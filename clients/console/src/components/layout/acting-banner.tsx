import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useMutation } from "@tanstack/react-query";
import { toast } from "sonner";
import { ArrowRight, LogOut, ShieldAlert, UserCog } from "lucide-react";
import { useAuth } from "@/auth/use-auth";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/cn";

/**
 * ActingBanner — shown across every page while the signed-in user is acting as someone
 * else: an operator inside another tenant through the token exchange, or an admin
 * impersonating one of their own users (ADR-0002, issue #9).
 *
 * Deliberately loud and always present: every request made from here is attributed to
 * that other user (with the actor recorded in `act_sub`), and nothing else on screen
 * would say whose data is on it. The countdown is the acting token's expiry — the server
 * clamps it, so it is the truth about how long this window lasts.
 *
 * It replaces the old ImpersonationBanner, which read `act_*` claims off the *stored*
 * access token. Nothing writes an acting token to storage any more (acting-store), so
 * those claims can no longer appear there.
 */
export function ActingBanner() {
  const { acting, exitTenant, user } = useAuth();
  const navigate = useNavigate();
  const remaining = useCountdown(acting?.expiresAt);

  const exit = useMutation({
    mutationFn: () => exitTenant(),
    onSuccess: () => {
      toast.success("Back in your own account");
      void navigate("/", { replace: true });
    },
    onError: () => toast.error("Could not end the session cleanly"),
  });

  if (!acting) return null;

  // Crossing a tenant boundary is the louder of the two: a root operator reached into
  // somebody else's tenant. Same-tenant impersonation stays on the warning tone.
  const crossTenant = user?.tenant !== undefined && user.tenant !== acting.tenantId;
  const tone = crossTenant ? "var(--color-destructive)" : "var(--color-warning)";

  return (
    <div
      role="status"
      aria-live="polite"
      data-testid="acting-banner"
      className={cn(
        "relative z-40 flex flex-wrap items-center gap-x-3 gap-y-2 overflow-hidden",
        "border-b px-4 py-2.5 text-sm sm:px-6",
      )}
      style={{
        borderColor: `oklch(from ${tone} l c h / 0.28)`,
        backgroundColor: "var(--color-muted)",
      }}
    >
      {crossTenant && (
        <span
          aria-hidden
          className="pointer-events-none absolute inset-y-0 left-0 w-[2px]"
          style={{ backgroundColor: tone }}
        />
      )}

      <span
        aria-hidden
        className="grid h-7 w-7 shrink-0 place-items-center rounded-xl"
        style={{
          backgroundColor: `oklch(from ${tone} l c h / 0.14)`,
          color: tone,
          boxShadow: `inset 0 0 0 1px oklch(from ${tone} l c h / 0.32)`,
        }}
      >
        <ShieldAlert className="h-3.5 w-3.5" />
      </span>

      <p className="flex min-w-0 flex-1 flex-wrap items-baseline gap-x-2 gap-y-1">
        <span
          className="text-[11px] font-semibold uppercase tracking-wider"
          style={{ color: tone }}
        >
          {crossTenant ? "Acting in another tenant" : "Impersonating"}
        </span>
        <span className="font-display truncate text-[14px] font-semibold tracking-tight">
          {acting.userName ?? acting.userId}
        </span>
        {crossTenant && user?.tenant && (
          <>
            <code className="code-chip">{user.tenant}</code>
            <ArrowRight className="h-3 w-3 shrink-0 opacity-70" style={{ color: tone }} aria-hidden />
          </>
        )}
        <code className="code-chip">{acting.tenantName ?? acting.tenantId}</code>
        <span className="hidden items-center gap-1 text-[12px] text-[var(--color-muted-foreground)] sm:inline-flex">
          <span>· as</span>
          <UserCog className="h-3 w-3" aria-hidden />
          <span className="text-[var(--color-foreground)]">{user?.name ?? user?.email ?? "you"}</span>
        </span>
        {remaining && (
          <span className="font-mono text-[11px] tabular-nums text-[var(--color-muted-foreground)]">
            {remaining} left
          </span>
        )}
      </p>

      <Button
        size="sm"
        variant="outline"
        onClick={() => exit.mutate()}
        disabled={exit.isPending}
        data-testid="acting-exit"
        className="shrink-0"
        style={{ borderColor: `oklch(from ${tone} l c h / 0.45)`, color: tone }}
      >
        <LogOut className="mr-1.5 h-3.5 w-3.5" />
        {exit.isPending ? "Leaving…" : "Exit"}
      </Button>
    </div>
  );
}

/** "12:34" until the acting token expires; null once there is nothing left to count. */
function useCountdown(expiresAt: string | undefined): string | null {
  const [label, setLabel] = useState<string | null>(null);

  useEffect(() => {
    if (!expiresAt) {
      setLabel(null);
      return;
    }

    const tick = () => {
      const msLeft = new Date(expiresAt).getTime() - Date.now();
      if (Number.isNaN(msLeft) || msLeft <= 0) {
        setLabel(null);
        return;
      }
      const totalSeconds = Math.floor(msLeft / 1000);
      const minutes = Math.floor(totalSeconds / 60);
      const seconds = totalSeconds % 60;
      setLabel(`${minutes}:${seconds.toString().padStart(2, "0")}`);
    };

    tick();
    const handle = setInterval(tick, 1000);
    return () => clearInterval(handle);
  }, [expiresAt]);

  return label;
}
