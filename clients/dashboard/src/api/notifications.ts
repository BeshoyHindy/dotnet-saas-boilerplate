import { api, unwrap, unwrapVoid, type Paged, type Schemas } from "@/lib/api-client";

export type NotificationDto = Schemas["NotificationDto"];

/** The caller's inbox, newest first, one page at a time. The server's page size defaults to 50. */
export async function listNotifications(
  params: { unreadOnly?: boolean; pageNumber?: number; pageSize?: number } = {},
): Promise<Paged<NotificationDto>> {
  return unwrap(
    await api.GET("/api/v1/notifications", {
      params: {
        query: {
          UnreadOnly: params.unreadOnly,
          PageNumber: params.pageNumber,
          PageSize: params.pageSize,
        },
      },
    }),
  );
}

export async function getUnreadCount(): Promise<number> {
  return Number(unwrap(await api.GET("/api/v1/notifications/unread-count", {})));
}

export async function markNotificationRead(notificationId: string): Promise<void> {
  unwrapVoid(
    await api.POST("/api/v1/notifications/{id}/read", {
      params: { path: { id: notificationId } },
    }),
  );
}

export async function markAllNotificationsRead(): Promise<void> {
  unwrapVoid(await api.POST("/api/v1/notifications/read-all", {}));
}
