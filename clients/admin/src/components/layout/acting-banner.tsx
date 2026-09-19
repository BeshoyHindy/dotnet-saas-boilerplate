import { useEffect, useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { toast } from "sonner";
import { LogOut, ShieldAlert } from "lucide-react";
import { useAuth } from "@/auth/use-auth";
import { Button } from "@/components/ui/button";

/**
 * ActingBanner — shown across every page while the operator is inside another tenant via a
 * token exchange (ADR-0002).
 *
 * It is deliberately loud and always present: every request the operator makes from here is
 * attributed to a user of that tenant (with the operator recorded in act_sub), and nothing else
 * on screen would tell them which tenant's data they are looking at.
 *
 * The countdown is the exchanged token's expiry — the server clamps it, so it is the truth about
 * how long this window lasts.
 */
export function ActingBanner() {
  const { acting, exitTenant } = useAuth();
  const remaining = useCountdown(acting?.expiresAt);

  const exit = useMutation({
    mutationFn: () => exitTenant(),
    onSuccess: () => toast.success("Back in your own account"),
    onError: () => toast.error("Could not end the tenant session cleanly"),
  });

  if (!acting) return null;

  return (
    <div
      role="status"
      aria-live="polite"
      data-testid="acting-banner"
      className="flex flex-wrap items-center gap-x-3 gap-y-2 border-b border-[var(--color-destructive)]/40 bg-[oklch(from_var(--color-destructive)_l_c_h_/_0.10)] px-4 py-2 text-sm"
    >
      <span
        aria-hidden
        className="grid h-6 w-6 shrink-0 place-items-center rounded-md bg-[var(--color-destructive)]/15 text-[var(--color-destructive)]"
      >
        <ShieldAlert className="h-3.5 w-3.5" />
      </span>

      <p className="min-w-0 flex-1">
        Acting in{" "}
        <code className="code-chip">{acting.tenantName ?? acting.tenantId}</code> as{" "}
        <code className="code-chip">{acting.userName ?? acting.userId}</code>
        {remaining && (
          <span className="ml-2 font-mono text-[11px] tabular-nums text-[var(--color-muted-foreground)]">
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
      >
        <LogOut className="mr-1 h-3.5 w-3.5" />
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
