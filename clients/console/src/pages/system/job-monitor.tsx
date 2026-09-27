import { useState } from "react";
import { ExternalLink, ListChecks } from "lucide-react";
import { issueJobMonitorAccess } from "@/api/job-monitor";
import { useAuth } from "@/auth/use-auth";
import { EntityPageHeader } from "@/components/list";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { ApiRequestError } from "@/lib/api-client";
import { SystemPermissions } from "@/lib/permissions";

/**
 * The Job monitor (Hangfire's dashboard) is served by the API, not by this app, so it
 * opens in its own tab (ADR-0009). Opening it asks the API for a short-lived cookie scoped
 * to `/jobs` on this origin, then points the new tab there.
 *
 * The tab is opened synchronously on the click and only navigated once the cookie is set:
 * a `window.open` after an `await` is no longer a user gesture, and popup blockers eat it.
 */
export function JobMonitorPage() {
  const { user } = useAuth();
  const canManage = (user?.permissions ?? []).includes(SystemPermissions.Hangfire.Manage);
  const [opening, setOpening] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function open() {
    setError(null);
    const tab = window.open("about:blank", "_blank");
    setOpening(true);
    try {
      const access = await issueJobMonitorAccess();
      if (tab) {
        // Sever the link back to this window before handing the tab to another page.
        tab.opener = null;
        tab.location.href = access.path;
      } else {
        window.open(access.path, "_blank", "noopener");
      }
    } catch (e) {
      tab?.close();
      setError(
        e instanceof ApiRequestError && e.status === 403
          ? "The Job monitor can't be opened while you are acting inside a tenant, or without the Hangfire permission."
          : "Couldn't open the Job monitor. Try again.",
      );
    } finally {
      setOpening(false);
    }
  }

  return (
    <div className="space-y-6 app-enter">
      <EntityPageHeader
        icon={ListChecks}
        title="Job monitor"
        description="Every tenant's background jobs: queued, scheduled, processing, succeeded and failed."
      >
        <Button onClick={open} disabled={opening}>
          <ExternalLink className="size-4" />
          {opening ? "Opening…" : "Open Job monitor"}
        </Button>
      </EntityPageHeader>

      <Card>
        <CardContent className="space-y-2 p-5 text-sm text-muted-foreground">
          <p>
            The Job monitor opens in a new tab. Access lasts 15 minutes from each time you open it
            and ends when you sign out; open it again from here when it runs out.
          </p>
          <p>
            {canManage
              ? "You can retry, delete and trigger jobs."
              : "It is read-only for you: retrying, deleting and triggering jobs needs the Manage Hangfire permission."}
          </p>
        </CardContent>
      </Card>

      {error ? (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      ) : null}
    </div>
  );
}
