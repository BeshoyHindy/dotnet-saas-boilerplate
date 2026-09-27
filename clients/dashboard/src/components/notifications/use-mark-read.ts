import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { markAllNotificationsRead, markNotificationRead } from "@/api/notifications";
import { ApiRequestError } from "@/lib/api-client";

function problemDetail(err: unknown): string {
  return err instanceof ApiRequestError
    ? err.problem?.detail ?? err.problem?.title ?? err.message
    : (err as Error).message;
}

/**
 * The bell's two "mark as read" mutations. A failure says so: otherwise the unread count
 * and the inbox stay silently wrong until the next fetch, with nothing to tell the user
 * their click did not land.
 */
export function useMarkNotificationsRead() {
  const queryClient = useQueryClient();

  const markAll = useMutation({
    mutationFn: markAllNotificationsRead,
    onSuccess: () => {
      queryClient.setQueryData(["notifications", "unread-count"], 0);
      void queryClient.invalidateQueries({ queryKey: ["notifications", "inbox"] });
    },
    onError: (err) => {
      toast.error("Could not mark notifications as read", { description: problemDetail(err) });
    },
  });

  const markOne = useMutation({
    mutationFn: (id: string) => markNotificationRead(id),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["notifications", "unread-count"] });
      void queryClient.invalidateQueries({ queryKey: ["notifications", "inbox"] });
    },
    onError: (err) => {
      toast.error("Could not mark the notification as read", { description: problemDetail(err) });
    },
  });

  return { markAll, markOne };
}
