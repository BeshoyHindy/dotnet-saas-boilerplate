import { api, AS_OPERATOR, unwrap, type Schemas } from "@/lib/api-client";

export type JobMonitorAccessResponse = Schemas["JobMonitorAccessResponse"];

/**
 * Ask the API for Job monitor access in this browser (ADR-0009). The response sets a
 * short-lived `HttpOnly; Secure; SameSite=Strict` cookie scoped to the Job monitor's path
 * (`/jobs`), bound to the operator's session; the body names that path and when the cookie
 * expires, never the credential itself. The console's nginx forwards `/jobs` to the API like
 * `/api`, so the cookie belongs to this origin only.
 *
 * Always sent with the OPERATOR's own token: the server refuses an acting token, and the Job
 * monitor is the operator's tool, not the impersonated user's.
 */
export async function issueJobMonitorAccess(): Promise<JobMonitorAccessResponse> {
  return unwrap(
    await api.POST("/api/v1/identity/operator/job-monitor-access", { headers: AS_OPERATOR }),
  );
}
