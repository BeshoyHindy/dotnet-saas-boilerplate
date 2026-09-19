import { useEffect, useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { toast } from "sonner";
import { DoorOpen, ShieldAlert } from "lucide-react";
import { useAuth } from "@/auth/use-auth";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogBody,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { ApiRequestError } from "@/lib/api-client";
import { cn } from "@/lib/cn";

type Props = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  tenantId: string;
  tenantName?: string;
  /** Called after the exchange succeeded and the acting session is live. */
  onEntered?: () => void;
};

const DURATION_OPTIONS = [10, 15, 30] as const;

/**
 * EnterTenantDialog — the operator's door into another tenant (ADR-0002 token exchange).
 *
 * The reason is mandatory because it is the only part of the audit row a human wrote. The
 * duration is a request, not a promise: the server clamps it to its configured ceiling.
 */
export function EnterTenantDialog({ open, onOpenChange, tenantId, tenantName, onEntered }: Props) {
  const { enterTenant } = useAuth();
  const [reason, setReason] = useState("");
  const [minutes, setMinutes] = useState<number>(15);

  useEffect(() => {
    if (open) {
      setReason("");
      setMinutes(15);
    }
  }, [open]);

  const trimmedReason = reason.trim();
  const reasonValid = trimmedReason.length >= 4;

  const mutation = useMutation({
    mutationFn: () =>
      enterTenant({
        tenantId,
        tenantName,
        reason: trimmedReason,
        durationMinutes: minutes,
      }),
    onSuccess: (session) => {
      toast.success(`Acting in ${tenantName ?? tenantId}`, {
        description: `As ${session.userName ?? session.userId}. Use Exit in the banner when you're done.`,
      });
      onOpenChange(false);
      onEntered?.();
    },
    onError: (err) => {
      const detail =
        err instanceof ApiRequestError
          ? err.problem?.detail ?? err.problem?.title ?? err.message
          : err instanceof Error
            ? err.message
            : "Unknown error";
      toast.error("Could not enter tenant", { description: detail });
    },
  });

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <div className="flex items-center gap-2">
            <span
              aria-hidden
              className="grid h-7 w-7 place-items-center rounded-md bg-[var(--color-accent-signal)]/15 text-[var(--color-accent-signal)]"
            >
              <DoorOpen className="h-4 w-4" />
            </span>
            <DialogTitle>Enter tenant</DialogTitle>
          </div>
          <DialogDescription>
            Act inside <code className="code-chip">{tenantName ?? tenantId}</code> as its admin. You
            keep your own session; the tenant token is short-lived and revocable.
          </DialogDescription>
        </DialogHeader>

        <DialogBody className="space-y-5">
          <fieldset className="space-y-2">
            <legend className="meta text-[var(--color-muted-foreground)]">// Duration</legend>
            <div className="grid grid-cols-3 gap-2">
              {DURATION_OPTIONS.map((option) => {
                const active = minutes === option;
                return (
                  <button
                    key={option}
                    type="button"
                    onClick={() => setMinutes(option)}
                    aria-pressed={active}
                    className={cn(
                      "flex flex-col items-center gap-0.5 rounded-md border px-3 py-2 transition-colors",
                      active
                        ? "border-[var(--color-accent-signal)] bg-[var(--color-accent-signal)]/10 text-[var(--color-foreground)]"
                        : "border-[var(--color-border)] hover:bg-[var(--color-muted)]/60",
                    )}
                  >
                    <span className="font-display text-lg font-semibold tabular-nums">{option}</span>
                    <span className="meta text-[var(--color-muted-foreground)]">minutes</span>
                  </button>
                );
              })}
            </div>
          </fieldset>

          <div className="space-y-1.5">
            <label htmlFor="enter-tenant-reason" className="meta text-[var(--color-muted-foreground)]">
              Reason
            </label>
            <textarea
              id="enter-tenant-reason"
              value={reason}
              onChange={(e) => setReason(e.target.value)}
              placeholder="e.g. Customer ticket #4821 — fixing their brand colours"
              rows={3}
              maxLength={500}
              className={cn(
                "w-full resize-y rounded-md border border-[var(--color-input)] bg-transparent px-3 py-2 text-sm",
                "transition-[border-color,background-color,box-shadow] duration-[var(--duration-fast)]",
                "hover:border-[var(--color-border-strong)]",
                "focus-visible:outline-none focus-visible:border-[var(--color-accent-signal)] focus-visible:ring-2 focus-visible:ring-[oklch(from_var(--color-accent-signal)_l_c_h_/_0.25)] focus-visible:bg-[var(--color-surface-2)]",
              )}
            />
            <p className="text-[11px] text-[var(--color-muted-foreground)]">
              Recorded in the security audit trail alongside your account and this tenant.
            </p>
          </div>

          <div className="flex items-start gap-2 rounded-md border border-[var(--color-warning)]/40 bg-[oklch(from_var(--color-warning)_l_c_h_/_0.08)] px-3 py-2.5 text-xs">
            <ShieldAlert className="mt-0.5 h-3.5 w-3.5 shrink-0 text-[var(--color-warning)]" />
            <div>
              <strong className="font-medium">You will be acting as a user of this tenant.</strong>{" "}
              Requests carry actor claims, so the audit trail shows both that user and you. The
              token is never stored — reloading the page returns you to your own account.
            </div>
          </div>
        </DialogBody>

        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button
            variant="signal"
            disabled={!reasonValid || mutation.isPending}
            onClick={() => mutation.mutate()}
            data-testid="enter-tenant-confirm"
          >
            {mutation.isPending ? "Exchanging token…" : `Enter for ${minutes} min`}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
