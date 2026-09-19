import { cloneElement, isValidElement, type ReactElement, type ReactNode } from "react";
import { Label } from "@/components/ui/label";
import { cn } from "@/lib/cn";

type FieldProps = {
  id: string;
  label: string;
  hint?: ReactNode;
  /** Validation message. Replaces the hint line and marks the control invalid. */
  error?: string;
  required?: boolean;
  className?: string;
  children: ReactNode;
};

/**
 * Form field wrapper used across editor dialogs and forms. Mono-caps tracked
 * label, an optional required dot announced to screen readers as "required",
 * and a hint or destructive error line below the control.
 *
 * The control is tied to its hint/error with `aria-describedby` and reflected as
 * `aria-invalid` automatically, so no caller has to wire it by hand; a
 * caller-supplied value always wins. Only a single element child is augmented.
 */
export function Field({ id, label, hint, error, required, className, children }: FieldProps) {
  const describedBy = error ? `${id}-error` : hint ? `${id}-hint` : undefined;
  const control = isValidElement(children)
    ? cloneElement(children as ReactElement<Record<string, unknown>>, {
        "aria-describedby":
          (children.props as Record<string, unknown>)["aria-describedby"] ?? describedBy,
        "aria-invalid":
          (children.props as Record<string, unknown>)["aria-invalid"] ?? (error ? true : undefined),
      })
    : children;

  return (
    <div className={cn("space-y-1.5", className)}>
      <Label
        htmlFor={id}
        className="flex items-center gap-1.5 text-[11.5px] font-semibold uppercase tracking-wider text-[var(--color-muted-foreground)]"
      >
        {label}
        {required && (
          <>
            <span aria-hidden className="text-[var(--color-destructive)]">·</span>
            <span className="sr-only">required</span>
          </>
        )}
      </Label>
      {control}
      {error ? (
        <p
          id={`${id}-error`}
          className="text-[11.5px] leading-relaxed text-[var(--color-destructive)]"
          role="alert"
        >
          {error}
        </p>
      ) : (
        hint && (
          <p
            id={`${id}-hint`}
            className="text-[11.5px] leading-relaxed text-[var(--color-muted-foreground)]/85"
          >
            {hint}
          </p>
        )
      )}
    </div>
  );
}
