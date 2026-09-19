import { useEffect, useState } from "react";
import { useMutation, useQuery, keepPreviousData } from "@tanstack/react-query";
import { ArrowLeft, ArrowRight, Check, DoorOpen, Search, ShieldAlert, UserCog } from "lucide-react";
import { toast } from "sonner";
import { searchUsers, type UserDto } from "@/api/identity";
import { useNavigate } from "react-router-dom";
import { useAuth } from "@/auth/use-auth";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import {
  Dialog,
  DialogBody,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { EnterTenantDialog } from "@/components/tenants/enter-tenant-dialog";
import { Monogram } from "@/components/monogram";
import { ApiRequestError } from "@/lib/api-client";
import { cn } from "@/lib/cn";

type Props = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  tenantId: string;
  tenantName?: string;
  /** Pre-select a user — skips the picker and jumps straight to the form. */
  prefillUser?: UserDto;
};

type DurationOption = { minutes: number; label: string };
const DURATION_OPTIONS: DurationOption[] = [
  { minutes: 10, label: "10 min" },
  { minutes: 15, label: "15 min" },
  { minutes: 30, label: "30 min" },
];

/**
 * ImpersonateDialog — two-step modal flow on the operator token exchange (ADR-0002, #9):
 *   1. Pick a user inside the target tenant (skipped if `prefillUser` is set)
 *   2. Enter reason + pick session duration → exchange
 *
 * Both steps need the exchange, because a caller only ever sees and acts inside the
 * tenant their token names:
 *   - listing the tenant's users needs an acting token for that tenant, so the picker
 *     asks the operator to ENTER the tenant first;
 *   - choosing a user then performs a FRESH exchange from the operator's own token with
 *     `targetUserId`, since an acting token may not be exchanged again (no nesting).
 *
 * The result is installed in place, in memory. There is one console (ADR-0004), so there
 * is nowhere to hand a token off to — and nothing is written to storage.
 */
export function ImpersonateDialog({
  open,
  onOpenChange,
  tenantId,
  tenantName,
  prefillUser,
}: Props) {
  const [step, setStep] = useState<"pick" | "configure">(prefillUser ? "configure" : "pick");
  const [selected, setSelected] = useState<UserDto | null>(prefillUser ?? null);

  // Reset on close + when prefill changes so reopening is idempotent.
  useEffect(() => {
    if (open) {
      setStep(prefillUser ? "configure" : "pick");
      setSelected(prefillUser ?? null);
    }
  }, [open, prefillUser]);

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent size="lg">
        <DialogHeader>
          <div className="flex items-center gap-2">
            <span
              aria-hidden
              className="grid h-7 w-7 place-items-center rounded-md bg-[var(--color-primary)]/15 text-[var(--color-primary)]"
            >
              <UserCog className="h-4 w-4" />
            </span>
            <DialogTitle>Impersonate user</DialogTitle>
          </div>
          <DialogDescription>
            Tenant{" "}
            <code className="code-chip">{tenantName ?? tenantId}</code> ·{" "}
            {step === "pick"
              ? "pick a user to act as."
              : "session details. The token is short-lived, audited and revocable."}
          </DialogDescription>
        </DialogHeader>

        {step === "pick" ? (
          <PickStep
            tenantId={tenantId}
            tenantName={tenantName}
            onPick={(user) => {
              setSelected(user);
              setStep("configure");
            }}
            onCancel={() => onOpenChange(false)}
          />
        ) : (
          selected && (
            <ConfigureStep
              tenantId={tenantId}
              tenantName={tenantName}
              user={selected}
              onBack={
                prefillUser
                  ? undefined
                  : () => {
                      setSelected(null);
                      setStep("pick");
                    }
              }
              onDone={() => onOpenChange(false)}
            />
          )
        )}
      </DialogContent>
    </Dialog>
  );
}

// ─── Step 1: pick user ──────────────────────────────────────────────────

function PickStep({
  tenantId,
  tenantName,
  onPick,
  onCancel,
}: {
  tenantId: string;
  tenantName?: string;
  onPick: (user: UserDto) => void;
  onCancel: () => void;
}) {
  const { acting } = useAuth();
  const [search, setSearch] = useState("");
  const [debounced, setDebounced] = useState("");
  const [enterOpen, setEnterOpen] = useState(false);

  const actingHere = acting?.tenantId === tenantId;

  useEffect(() => {
    const handle = setTimeout(() => setDebounced(search.trim()), 250);
    return () => clearTimeout(handle);
  }, [search]);

  const query = useQuery({
    // The acting session's jti is part of the key: users listed under one acting token
    // must not be served from cache under another (or to the operator's own tenant).
    queryKey: ["impersonation", "users", tenantId, acting?.jti ?? "none", debounced],
    queryFn: () =>
      // No tenant filter: a caller cannot scope a request to another tenant (ADR-0002).
      // The search runs inside the tenant the CURRENT token names, which is why it is
      // only enabled while acting inside the target one.
      searchUsers({
        search: debounced || undefined,
        pageSize: 25,
        // Skip disabled accounts — impersonating a deactivated user is a footgun
        // (the impersonation token would be valid, but the user's normal sign-in
        // is disabled — confusing to debug).
        isActive: true,
      }),
    enabled: actingHere,
    placeholderData: keepPreviousData,
  });

  const users = query.data?.items ?? [];

  if (!actingHere) {
    return (
      <>
        <DialogBody className="space-y-3">
          <div
            role="status"
            className="flex flex-wrap items-center gap-3 rounded-md border border-[var(--color-border)] bg-[var(--color-muted)] px-4 py-3 text-[13px] leading-relaxed text-[var(--color-muted-foreground)]"
          >
            <p className="min-w-0 flex-1">
              Enter <code className="code-chip">{tenantName ?? tenantId}</code> to list its users —
              a token only ever sees the tenant it names. You can leave again from the banner.
            </p>
            <Button
              variant="outline"
              size="sm"
              onClick={() => setEnterOpen(true)}
              data-testid="impersonate-enter-tenant"
            >
              <DoorOpen className="mr-1.5 h-3.5 w-3.5" />
              Enter tenant
            </Button>
          </div>
        </DialogBody>

        <DialogFooter>
          <Button variant="outline" onClick={onCancel}>
            Cancel
          </Button>
        </DialogFooter>

        <EnterTenantDialog
          open={enterOpen}
          onOpenChange={setEnterOpen}
          tenantId={tenantId}
          tenantName={tenantName}
        />
      </>
    );
  }

  return (
    <>
      <DialogBody className="space-y-3">
        <div className="relative">
          <Search className="pointer-events-none absolute left-3 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-[var(--color-muted-foreground)]" />
          <Input
            autoFocus
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Search by name, email, or username…"
            aria-label="Search users to impersonate"
            className="pl-9"
          />
        </div>

        <div className="-mx-2 max-h-[22rem] overflow-y-auto">
          {query.isError && (
            <div className="px-3 py-6 text-sm text-[var(--color-destructive)]">
              {query.error instanceof ApiRequestError
                ? query.error.problem?.detail ?? query.error.message
                : "Failed to load users."}
            </div>
          )}

          {query.isLoading && (
            <div className="px-3 py-10 text-center font-mono text-xs uppercase tracking-[0.18em] text-[var(--color-muted-foreground)]">
              Loading
              <span className="caret text-[var(--color-primary)]" aria-hidden />
            </div>
          )}

          {!query.isLoading && users.length === 0 && (
            <div className="px-3 py-10 text-center text-sm text-[var(--color-muted-foreground)]">
              No users match{debounced ? ` “${debounced}”` : ""}.
            </div>
          )}

          <ul className="divide-y divide-[var(--color-border)]">
            {users.map((user) => (
              <li key={user.id}>
                <button
                  type="button"
                  onClick={() => onPick(user)}
                  className="group flex w-full items-center gap-3 px-3 py-2.5 text-left transition-colors hover:bg-[var(--color-muted)]/60 focus:outline-none focus-visible:bg-[var(--color-muted)]/60"
                >
                  <Monogram
                    seed={user.id ?? user.userName ?? "x"}
                    firstName={user.firstName ?? undefined}
                    lastName={user.lastName ?? undefined}
                    fallback={user.userName ?? user.email ?? "?"}
                    size="sm"
                  />
                  <div className="min-w-0 flex-1">
                    <div className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5">
                      <span className="truncate text-sm font-medium">
                        {[user.firstName, user.lastName].filter(Boolean).join(" ") ||
                          user.userName ||
                          user.email ||
                          "Unnamed"}
                      </span>
                      {user.userName && (
                        <span className="truncate font-mono text-[11px] text-[var(--color-muted-foreground)]">
                          @{user.userName}
                        </span>
                      )}
                    </div>
                    <div className="truncate font-mono text-[11px] text-[var(--color-muted-foreground)]">
                      {user.email ?? "—"}
                    </div>
                  </div>
                  <ArrowRight className="h-3.5 w-3.5 text-[var(--color-muted-foreground)] transition-transform group-hover:translate-x-0.5" />
                </button>
              </li>
            ))}
          </ul>
        </div>
      </DialogBody>

      <DialogFooter>
        <Button variant="outline" onClick={onCancel}>
          Cancel
        </Button>
      </DialogFooter>
    </>
  );
}

// ─── Step 2: configure ──────────────────────────────────────────────────

function ConfigureStep({
  tenantId,
  tenantName,
  user,
  onBack,
  onDone,
}: {
  tenantId: string;
  tenantName?: string;
  user: UserDto;
  onBack?: () => void;
  onDone: () => void;
}) {
  const [reason, setReason] = useState("");
  const [minutes, setMinutes] = useState<number>(15);

  const trimmedReason = reason.trim();
  const reasonValid = trimmedReason.length >= 4;

  const { enterTenant } = useAuth();
  const navigate = useNavigate();

  const mutation = useMutation<unknown, Error, void>({
    mutationFn: () =>
      // A FRESH exchange from the operator's own token (`AS_OPERATOR` inside
      // `exchangeOperatorToken`): the picker above ran under an acting token, and an
      // acting token may not be exchanged again. The result replaces the acting session
      // in memory — same tenant, now as the chosen user.
      enterTenant({
        tenantId,
        tenantName,
        targetUserId: user.id ?? "",
        reason: trimmedReason,
        durationMinutes: minutes,
      }),
    onSuccess: () => {
      toast.success(`Acting as ${labelFor(user)} · up to ${minutes} min`, {
        description: "End it from the banner at the top, or revoke the grant here.",
      });
      onDone();
      void navigate("/");
    },
    onError: (err) => {
      const detail =
        err instanceof ApiRequestError
          ? err.problem?.detail ?? err.problem?.title ?? err.message
          : err.message;
      toast.error("Impersonation failed", { description: detail });
    },
  });

  return (
    <>
      <DialogBody className="space-y-5">
        <SelectedUserCard user={user} tenantId={tenantId} tenantName={tenantName} />

        <fieldset className="space-y-2">
          <legend className="meta text-[var(--color-muted-foreground)]">// Duration</legend>
          <div className="grid grid-cols-3 gap-2">
            {DURATION_OPTIONS.map((opt) => {
              const active = minutes === opt.minutes;
              return (
                <button
                  key={opt.minutes}
                  type="button"
                  onClick={() => setMinutes(opt.minutes)}
                  aria-pressed={active}
                  className={cn(
                    "flex flex-col items-center gap-0.5 rounded-md border px-3 py-2 transition-colors",
                    active
                      ? "border-[var(--color-primary)] bg-[var(--color-primary)]/10 text-[var(--color-foreground)]"
                      : "border-[var(--color-border)] hover:bg-[var(--color-muted)]/60",
                  )}
                >
                  <span className="font-display text-lg font-semibold tabular-nums">
                    {opt.minutes}
                  </span>
                  <span className="meta text-[var(--color-muted-foreground)]">minutes</span>
                </button>
              );
            })}
          </div>
        </fieldset>

        <div className="space-y-1.5">
          <label
            htmlFor="impersonation-reason"
            className="meta flex items-center gap-1.5 text-[var(--color-muted-foreground)]"
          >
            Reason
            <span className="text-[var(--color-destructive)]" aria-hidden>
              ·
            </span>
          </label>
          <textarea
            id="impersonation-reason"
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            placeholder="e.g. Customer ticket #4821 — verifying ledger discrepancy"
            rows={3}
            maxLength={500}
            className={cn(
              "w-full resize-y rounded-md border border-[var(--color-input)] bg-transparent px-3 py-2 text-sm",
              "transition-[border-color,background-color,box-shadow] duration-[var(--duration-fast)]",
              "hover:border-[var(--color-border-strong)]",
              "focus-visible:outline-none focus-visible:border-[var(--color-primary)] focus-visible:ring-2 focus-visible:ring-[oklch(from_var(--color-primary)_l_c_h_/_0.25)] focus-visible:bg-[var(--color-surface-2)]",
            )}
          />
          <p className="flex items-center justify-between text-[11px] text-[var(--color-muted-foreground)]">
            <span>
              Recorded in the security audit trail — be specific enough that a reviewer can
              reconstruct the case later.
            </span>
            <span
              className={cn(
                "font-mono tabular-nums",
                trimmedReason.length > 0 && !reasonValid && "text-[var(--color-warning)]",
              )}
            >
              {trimmedReason.length}/500
            </span>
          </p>
        </div>

        <div className="flex items-start gap-2 rounded-md border border-[var(--color-warning)]/40 bg-[oklch(from_var(--color-warning)_l_c_h_/_0.08)] px-3 py-2.5 text-xs text-[var(--color-foreground)]">
          <ShieldAlert className="mt-0.5 h-3.5 w-3.5 shrink-0 text-[var(--color-warning)]" />
          <div>
            <strong className="font-medium">Everything you do is attributed to your account.</strong>
            {" "}The session token carries actor claims; the audit trail will show
            both the user being acted as and you as the actor. The token is never stored —
            reloading the page returns you to your own account.
          </div>
        </div>
      </DialogBody>

      <DialogFooter>
        {onBack && (
          <Button variant="outline" onClick={onBack} disabled={mutation.isPending} className="sm:mr-auto">
            <ArrowLeft className="mr-1 h-3.5 w-3.5" /> Choose another user
          </Button>
        )}
        <Button
          variant="default"
          disabled={!reasonValid || mutation.isPending}
          onClick={() => mutation.mutate()}
        >
          {mutation.isPending ? (
            "Issuing token…"
          ) : (
            <>
              <Check className="mr-1 h-3.5 w-3.5" /> Act as this user for {minutes} min
            </>
          )}
        </Button>
      </DialogFooter>
    </>
  );
}

function SelectedUserCard({
  user,
  tenantId,
  tenantName,
}: {
  user: UserDto;
  tenantId: string;
  tenantName?: string;
}) {
  return (
    <div className="flex items-center gap-3 rounded-md border border-[var(--color-border)] bg-[var(--color-surface-2)] px-3 py-3">
      <Monogram
        seed={user.id ?? user.userName ?? "x"}
        firstName={user.firstName ?? undefined}
        lastName={user.lastName ?? undefined}
        fallback={user.userName ?? user.email ?? "?"}
        size="md"
      />
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-baseline gap-2">
          <span className="truncate text-sm font-medium">{labelFor(user)}</span>
          {user.userName && (
            <span className="truncate font-mono text-[11px] text-[var(--color-muted-foreground)]">
              @{user.userName}
            </span>
          )}
        </div>
        <div className="mt-0.5 flex flex-wrap items-baseline gap-2 font-mono text-[11px] text-[var(--color-muted-foreground)]">
          <span className="truncate">{user.email ?? "—"}</span>
          <Badge variant="muted" className="font-mono uppercase tracking-[0.14em]">
            {tenantName ?? tenantId}
          </Badge>
        </div>
      </div>
    </div>
  );
}

function labelFor(user: UserDto): string {
  return (
    [user.firstName, user.lastName].filter(Boolean).join(" ").trim() ||
    user.userName ||
    user.email ||
    "Unnamed user"
  );
}
