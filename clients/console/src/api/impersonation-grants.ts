import { api, unwrap, type Schemas } from "@/lib/api-client";

export type ImpersonationGrantStatus = Schemas["ImpersonationGrantStatus"];
export type ImpersonationGrantDto = Schemas["ImpersonationGrantDto"];

export type ListGrantsParams = {
  status?: ImpersonationGrantStatus;
  impersonatedTenantId?: string;
  actorUserId?: string;
  take?: number;
};

export async function listImpersonationGrants(
  params: ListGrantsParams = {},
): Promise<ImpersonationGrantDto[]> {
  return unwrap(
    await api.GET("/api/v1/identity/impersonation/grants", {
      params: {
        query: {
          Status: params.status,
          ImpersonatedTenantId: params.impersonatedTenantId,
          ActorUserId: params.actorUserId,
          Take: params.take ?? 100,
        },
      },
    }),
  );
}

export async function revokeImpersonationGrant(
  id: string,
  reason?: string,
): Promise<ImpersonationGrantDto> {
  return unwrap(
    await api.POST("/api/v1/identity/impersonation/grants/{id}/revoke", {
      params: { path: { id } },
      body: { reason: reason ?? null },
    }),
  );
}
