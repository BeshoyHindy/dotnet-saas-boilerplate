/**
 * The "acting" layer (ADR-0002, issue #9).
 *
 * While someone is acting as another user they hold TWO credentials: their own session
 * (localStorage, refreshable — untouched) and a short-lived token naming the subject
 * they act as. This store holds the second one.
 *
 * It is deliberately module-scope memory, never localStorage:
 *   - the acting token is access-only, so it cannot be refreshed — persisting it would
 *     only ever resurrect a dead credential after a reload;
 *   - "acting inside a customer tenant" should not survive a browser restart; and
 *   - a token that names someone else's tenant is exactly the thing you do not want
 *     sitting in a storage bucket every script on the origin can read.
 *
 * Consequence, by design: a page reload drops back to the operator's own session. That
 * replaces the console's old localStorage impersonation stash, which kept the exchanged
 * token — and the operator's own — in storage across reloads.
 */

export type ActingSession = {
  /** Acting access token. Sent instead of the operator's own token while this is set. */
  accessToken: string;
  tenantId: string;
  tenantName?: string;
  /** The user the token acts as. */
  userId: string;
  userName?: string;
  /** ISO timestamp; the server clamps this to its configured ceiling. */
  expiresAt: string;
  /** jti of the token == id key of the revocable grant. */
  jti?: string;
  /** Grant row id — what the grants list revokes. Absent on same-tenant impersonation,
   *  whose response does not carry one. */
  grantId?: string;
};

type Listener = () => void;

let session: ActingSession | null = null;
/** Set when the acting session ended involuntarily, so the UI can say why. */
let notice: string | null = null;

const listeners = new Set<Listener>();

function emit() {
  for (const listener of listeners) listener();
}

export const actingStore = {
  get: (): ActingSession | null => session,
  getNotice: (): string | null => notice,

  /** True when the caller is acting inside `tenantId` right now. */
  isActingIn(tenantId: string): boolean {
    return session?.tenantId === tenantId;
  },

  start(next: ActingSession) {
    session = next;
    notice = null;
    emit();
  },

  /** Deliberate exit ("Exit"). No notice — the operator asked for it. */
  clear() {
    if (session === null && notice === null) return;
    session = null;
    notice = null;
    emit();
  },

  /**
   * Involuntary end: the acting token was revoked, expired, or rejected. The operator's
   * own session is untouched — we simply stop sending the acting token and say so.
   */
  drop(reason: string) {
    if (session === null) return;
    session = null;
    notice = reason;
    emit();
  },

  consumeNotice(): string | null {
    const value = notice;
    notice = null;
    return value;
  },

  subscribe(listener: Listener) {
    listeners.add(listener);
    return () => {
      listeners.delete(listener);
    };
  },
};
