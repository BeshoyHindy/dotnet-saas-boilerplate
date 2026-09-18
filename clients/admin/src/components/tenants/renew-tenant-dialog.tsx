import { useEffect, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { CalendarClock } from "lucide-react";
import { toast } from "sonner";
import { renewTenant } from "@/api/tenants";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Field } from "@/components/list";
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

function formatDate(value?: string | null): string {
  if (!value) return "—";
  const d = new Date(value);
  return Number.isNaN(d.getTime()) ? value : d.toLocaleDateString();
}

/**
 * Renew a tenant's validity. Leave the term blank to use the server's default
 * extension, or specify a number of months (1–120) to stack on top of the
 * tenant's remaining time.
 */
export function RenewTenantDialog({
  open,
  onOpenChange,
  tenantId,
  validUpto,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  tenantId: string;
  validUpto?: string;
}) {
  const queryClient = useQueryClient();
  const [months, setMonths] = useState<string>("");

  // Reset the field each time the dialog opens.
  useEffect(() => {
    if (open) setMonths("");
  }, [open]);

  const parsedMonths = months.trim() ? Number(months) : null;
  const monthsInvalid =
    parsedMonths !== null &&
    (!Number.isFinite(parsedMonths) || parsedMonths < 1 || parsedMonths > 120);

  const mutation = useMutation({
    mutationFn: (value: number | null) => renewTenant(tenantId, value),
    onSuccess: (result) => {
      toast.success("Tenant renewed", {
        description: `Valid until ${formatDate(result.validUpto)}.`,
      });
      queryClient.invalidateQueries({ queryKey: ["tenant", tenantId] });
      queryClient.invalidateQueries({ queryKey: ["tenants"] });
      onOpenChange(false);
    },
    onError: (err) => {
      const detail =
        err instanceof ApiRequestError
          ? err.problem?.detail ?? err.problem?.title ?? err.message
          : (err as Error).message;
      toast.error("Renew failed", { description: detail });
    },
  });

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent size="md">
        <DialogHeader>
          <div className="flex items-center gap-3">
            <span
              aria-hidden
              className="grid h-9 w-9 shrink-0 place-items-center rounded-xl
                bg-[oklch(from_var(--color-primary)_l_c_h_/_0.12)] text-[var(--color-primary)]
                ring-1 ring-inset ring-[oklch(from_var(--color-primary)_l_c_h_/_0.18)]"
            >
              <CalendarClock className="h-[18px] w-[18px]" />
            </span>
            <DialogTitle className="text-[16px]">Renew tenant</DialogTitle>
          </div>
          <DialogDescription className="mt-1">
            Extend the tenant's validity, stacking on any remaining time. Currently valid until{" "}
            {formatDate(validUpto)}.
          </DialogDescription>
        </DialogHeader>

        <DialogBody className="space-y-4">
          <Field
            id="renew-months"
            label="Months"
            hint="Optional. Leave blank to use the server's default extension. 1–120."
            error={monthsInvalid ? "Enter a number of months between 1 and 120." : undefined}
          >
            <Input
              id="renew-months"
              type="number"
              min={1}
              max={120}
              inputMode="numeric"
              placeholder="Default"
              value={months}
              onChange={(e) => setMonths(e.target.value)}
            />
          </Field>
        </DialogBody>

        <DialogFooter>
          <Button type="button" variant="outline" onClick={() => onOpenChange(false)} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button
            type="button"
            onClick={() => mutation.mutate(parsedMonths)}
            disabled={mutation.isPending || monthsInvalid}
          >
            {mutation.isPending ? "Renewing…" : "Renew"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
