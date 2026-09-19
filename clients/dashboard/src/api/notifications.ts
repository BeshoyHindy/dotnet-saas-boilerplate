import { api, unwrap, unwrapVoid, type Schemas } from "@/lib/api-client";

export type NotificationDto = Schemas["NotificationDto"];

export async function listNotifications(
  params: { unreadOnly?: boolean; page?: number; pageSize?: number } = {},
): Promise<NotificationDto[]> {
  return unwrap(
    await api.GET("/api/v1/notifications", {
      params: {
        query: {
          unreadOnly: params.unreadOnly,
          page: params.page,
          pageSize: params.pageSize,
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
