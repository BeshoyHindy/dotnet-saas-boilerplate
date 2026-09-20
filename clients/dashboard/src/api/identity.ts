import { api, unwrap, unwrapVoid, type Paged, type Schemas } from "@/lib/api-client";

// -----------------------------
// Types — every one of them is the generated contract type (ADR-0004).
// -----------------------------

export type UserDto = Schemas["UserDto"];
export type UserRoleDto = Schemas["UserRoleDto"];
export type RoleDto = Schemas["RoleDto"];
export type RegisterUserInput = Schemas["RegisterUserCommand"];
export type RegisterUserResponse = Schemas["RegisterUserResponse"];
export type UpsertRoleInput = Schemas["UpsertRoleCommand"];
export type PermissionCatalogEntryDto = Schemas["PermissionCatalogEntryDto"];
export type AdminUserSessionDto = Schemas["UserSessionDto"];
export type GroupDto = Schemas["GroupDto"];
export type GroupMemberDto = Schemas["GroupMemberDto"];
export type CreateGroupInput = Schemas["CreateGroupCommand"];
export type UpdateGroupInput = Schemas["UpdateGroupRequest"];
export type TwoFactorEnrollmentResponse = Schemas["TwoFactorEnrollmentResponse"];

export type SearchUsersParams = {
  pageNumber?: number;
  pageSize?: number;
  sort?: string;
  search?: string;
  isActive?: boolean | null;
  emailConfirmed?: boolean | null;
  roleId?: string | null;
};

// -----------------------------
// Users
// -----------------------------

export async function searchUsers(params: SearchUsersParams = {}): Promise<Paged<UserDto>> {
  return unwrap(
    await api.GET("/api/v1/identity/users/search", {
      params: {
        query: {
          PageNumber: params.pageNumber ?? 1,
          PageSize: params.pageSize ?? 20,
          Sort: params.sort,
          Search: params.search,
          IsActive: params.isActive ?? undefined,
          EmailConfirmed: params.emailConfirmed ?? undefined,
          RoleId: params.roleId ?? undefined,
        },
      },
    }),
  );
}

export async function getUserById(id: string): Promise<UserDto> {
  return unwrap(await api.GET("/api/v1/identity/users/{id}", { params: { path: { id } } }));
}

export async function getUserRoles(id: string): Promise<UserRoleDto[]> {
  return unwrap(await api.GET("/api/v1/identity/users/{id}/roles", { params: { path: { id } } }));
}

export async function assignUserRoles(userId: string, userRoles: UserRoleDto[]): Promise<string> {
  return unwrap(
    await api.POST("/api/v1/identity/users/{id}/roles", {
      params: { path: { id: userId } },
      body: { userId, userRoles },
    }),
  );
}

export async function toggleUserStatus(userId: string, activate: boolean): Promise<void> {
  unwrapVoid(
    await api.PATCH("/api/v1/identity/users/{id}", {
      params: { path: { id: userId } },
      body: { userId, activateUser: activate },
    }),
  );
}

export async function deleteUser(userId: string): Promise<void> {
  unwrapVoid(await api.DELETE("/api/v1/identity/users/{id}", { params: { path: { id: userId } } }));
}

export async function confirmUserEmail(userId: string): Promise<void> {
  unwrapVoid(
    await api.POST("/api/v1/identity/users/{id}/confirm-email", {
      params: { path: { id: userId } },
    }),
  );
}

export async function resendUserConfirmationEmail(userId: string): Promise<void> {
  unwrapVoid(
    await api.POST("/api/v1/identity/users/{id}/resend-confirmation-email", {
      params: { path: { id: userId } },
    }),
  );
}

// There is no `setProfileImage(url)` any more (#83). PUT /identity/profile/image accepted any
// string, so a user could point their avatar at another user's — and the next replace deleted that
// other user's object, which the tenant legitimately owned. An avatar is now uploaded with
// `updateMyProfile({ image })` and cleared with `updateMyProfile({ deleteCurrentImage: true })`;
// the URL is the server's answer, never the client's request.

/**
 * The signed-in user's effective permissions. The JWT carries only role names;
 * permissions are resolved server-side per role here. The auth context calls
 * this after login and on subject changes so gated UI reflects the live grants.
 */
export async function getMyPermissions(): Promise<string[]> {
  return unwrap(await api.GET("/api/v1/identity/permissions", {})) ?? [];
}

/** The authenticated user's full profile (name, email, phone, imageUrl, …). */
export async function getMyProfile(): Promise<UserDto> {
  return unwrap(await api.GET("/api/v1/identity/profile", {}));
}

export async function registerUser(input: RegisterUserInput): Promise<RegisterUserResponse> {
  return unwrap(await api.POST("/api/v1/identity/register", { body: input }));
}

// -----------------------------
// Roles
// -----------------------------

export async function listRoles(): Promise<RoleDto[]> {
  const page = unwrap(
    await api.GET("/api/v1/identity/roles", { params: { query: { PageSize: 200 } } }),
  );
  return page.items ?? [];
}

export async function getRoleWithPermissions(id: string): Promise<RoleDto> {
  return unwrap(await api.GET("/api/v1/identity/{id}/permissions", { params: { path: { id } } }));
}

export async function upsertRole(input: UpsertRoleInput): Promise<RoleDto> {
  return unwrap(await api.POST("/api/v1/identity/roles", { body: input }));
}

export async function updateRolePermissions(
  roleId: string,
  permissions: string[],
): Promise<string> {
  return unwrap(
    await api.PUT("/api/v1/identity/{id}/permissions", {
      params: { path: { id: roleId } },
      body: { roleId, permissions },
    }),
  );
}

export async function deleteRole(id: string): Promise<void> {
  unwrapVoid(await api.DELETE("/api/v1/identity/roles/{id}", { params: { path: { id } } }));
}

/**
 * The host's permission catalog — the single source of truth for the role editor.
 * Every module's permissions land here at startup, filtered to the caller's tenant
 * context (Admin set for regular tenants; Admin + Root set for the root tenant).
 */
export async function getPermissionsCatalog(): Promise<PermissionCatalogEntryDto[]> {
  return unwrap(await api.GET("/api/v1/identity/permissions/catalog", {}));
}

// -----------------------------
// User sessions (admin)
// -----------------------------

export async function getUserSessionsAdmin(userId: string): Promise<AdminUserSessionDto[]> {
  return unwrap(
    await api.GET("/api/v1/identity/users/{userId}/sessions", { params: { path: { userId } } }),
  );
}

export async function adminRevokeUserSession(userId: string, sessionId: string): Promise<void> {
  unwrapVoid(
    await api.DELETE("/api/v1/identity/users/{userId}/sessions/{sessionId}", {
      params: { path: { userId, sessionId } },
    }),
  );
}

export async function adminRevokeAllUserSessions(
  userId: string,
): Promise<Schemas["RevokeSessionsResponse"]> {
  return unwrap(
    await api.POST("/api/v1/identity/users/{userId}/sessions/revoke-all", {
      params: { path: { userId } },
      body: { userId, reason: null },
    }),
  );
}

// -----------------------------
// Groups
// -----------------------------

export async function listGroups(search?: string): Promise<GroupDto[]> {
  return unwrap(await api.GET("/api/v1/identity/groups", { params: { query: { search } } }));
}

export async function getGroupById(id: string): Promise<GroupDto> {
  return unwrap(await api.GET("/api/v1/identity/groups/{id}", { params: { path: { id } } }));
}

export async function createGroup(input: CreateGroupInput): Promise<GroupDto> {
  return unwrap(await api.POST("/api/v1/identity/groups", { body: input }));
}

export async function updateGroup(id: string, input: UpdateGroupInput): Promise<GroupDto> {
  return unwrap(
    await api.PUT("/api/v1/identity/groups/{id}", { params: { path: { id } }, body: input }),
  );
}

export async function deleteGroup(id: string): Promise<void> {
  unwrapVoid(await api.DELETE("/api/v1/identity/groups/{id}", { params: { path: { id } } }));
}

export async function getGroupMembers(groupId: string): Promise<GroupMemberDto[]> {
  return unwrap(
    await api.GET("/api/v1/identity/groups/{groupId}/members", { params: { path: { groupId } } }),
  );
}

export async function addUsersToGroup(
  groupId: string,
  userIds: string[],
): Promise<Schemas["AddUsersToGroupResponse"]> {
  return unwrap(
    await api.POST("/api/v1/identity/groups/{groupId}/members", {
      params: { path: { groupId } },
      body: { userIds },
    }),
  );
}

export async function removeUserFromGroup(groupId: string, userId: string): Promise<void> {
  unwrapVoid(
    await api.DELETE("/api/v1/identity/groups/{groupId}/members/{userId}", {
      params: { path: { groupId, userId } },
    }),
  );
}

// -----------------------------
// Profile update (PUT /identity/profile)
// -----------------------------

export type UpdateProfileInput = {
  firstName?: string | null;
  lastName?: string | null;
  phoneNumber?: string | null;
  /**
   * A new avatar, as raw bytes. The server uploads it with `IStorageService.UploadAsync` into the
   * `uploads/` prefix and persists the durable unsigned URL that comes back. This is the ONE way
   * the dashboard uploads an avatar: a Files-module `publicUrl` is a presigned GET that expires in
   * minutes, so storing one on the column stores a dead link (issue #72).
   */
  image?: Schemas["FileUploadRequest"] | null;
  /** Delete the current avatar — clears the column AND removes the stored object. */
  deleteCurrentImage?: boolean;
};

/**
 * Updates the authenticated user's profile. Email changes go through their own dedicated
 * endpoint. Reads the current profile first so unset optional fields keep their existing
 * values instead of being nulled.
 */
export async function updateMyProfile(input: UpdateProfileInput): Promise<void> {
  const profile = await getMyProfile();
  unwrapVoid(
    await api.PUT("/api/v1/identity/profile", {
      body: {
        id: profile.id ?? "",
        firstName: input.firstName ?? profile.firstName ?? null,
        lastName: input.lastName ?? profile.lastName ?? null,
        phoneNumber: input.phoneNumber ?? profile.phoneNumber ?? null,
        email: profile.email,
        image: input.image ?? null,
        deleteCurrentImage: input.deleteCurrentImage ?? false,
      },
    }),
  );
}

// -----------------------------
// Password reset trio (anonymous; the tenant is a path segment)
// -----------------------------

/**
 * Step 1 of the forgot-password flow. The server resolves the user by (email, tenant),
 * generates a reset token and mails a link to the dashboard's `/reset-password` page.
 * It always answers 200, whether or not the address exists — never leak account presence.
 */
export async function requestPasswordReset(input: { email: string; tenant: string }): Promise<void> {
  unwrapVoid(
    await api.POST("/api/v1/tenants/{tenant}/auth/forgot-password", {
      params: { path: { tenant: input.tenant } },
      body: { email: input.email },
    }),
  );
}

/**
 * Step 2 of the forgot-password flow. The caller carries (token, email, tenant) from the
 * mailed link plus a new password from the form. Existing access tokens stay valid until
 * their natural expiry — the UI bounces the user to /login for a fresh session.
 */
export async function resetPassword(input: {
  email: string;
  password: string;
  token: string;
  tenant: string;
}): Promise<void> {
  unwrapVoid(
    await api.POST("/api/v1/tenants/{tenant}/auth/reset-password", {
      params: { path: { tenant: input.tenant } },
      body: { email: input.email, password: input.password, token: input.token },
    }),
  );
}

/**
 * Confirm-email landing. The tenant is a path segment; (userId, code) come as query
 * parameters from the mailed link, which points at the dashboard's own `/confirm-email`
 * page (issue #46). Returns the server's confirmation message.
 */
export async function confirmEmail(input: {
  userId: string;
  code: string;
  tenant: string;
}): Promise<string> {
  return unwrap(
    await api.GET("/api/v1/tenants/{tenant}/auth/confirm-email", {
      params: {
        path: { tenant: input.tenant },
        query: { userId: input.userId, code: input.code },
      },
      parseAs: "text",
    }),
  );
}

// -----------------------------
// Password change (authenticated)
// -----------------------------

export async function changePassword(input: {
  password: string;
  newPassword: string;
  confirmNewPassword: string;
}): Promise<void> {
  unwrapVoid(await api.POST("/api/v1/identity/change-password", { body: input }));
}

// -----------------------------
// Two-factor enrollment (TOTP)
// -----------------------------

/**
 * Begin (or rotate) TOTP enrollment. 2FA is NOT enabled until the user confirms with a
 * code via verifyEnrollTwoFactor — this just hands back the secret + otpauth:// URI so
 * the QR can render.
 */
export async function enrollTwoFactor(): Promise<TwoFactorEnrollmentResponse> {
  return unwrap(await api.POST("/api/v1/identity/2fa/enroll", {}));
}

export async function verifyEnrollTwoFactor(
  code: string,
): Promise<Schemas["TwoFactorOperationResponse"]> {
  return unwrap(await api.POST("/api/v1/identity/2fa/verify", { body: { code } }));
}

export async function disableTwoFactor(
  currentPassword: string,
): Promise<Schemas["TwoFactorOperationResponse"]> {
  return unwrap(await api.POST("/api/v1/identity/2fa/disable", { body: { currentPassword } }));
}
