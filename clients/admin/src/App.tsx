import { Suspense } from "react";
import { RouterProvider } from "react-router-dom";
import { QueryClientProvider } from "@tanstack/react-query";
import { Toaster } from "sonner";
import { AlertCircle, AlertTriangle, CheckCircle2, Info, Loader2 } from "lucide-react";
import { queryClient } from "@/lib/query-client";
import { AuthProvider } from "@/auth/auth-context";
import { ThemeProvider, useTheme } from "@/components/theme/theme-provider";
import { router } from "@/routes";

export function App() {
  return (
    <ThemeProvider>
      <QueryClientProvider client={queryClient}>
        <AuthProvider>
          {/* Top-level boundary so the public lazy routes (login, password
              reset, confirm-email) have a Suspense ancestor on cold chunk
              fetch — the protected routes also have AppShell's own. */}
          <Suspense
            fallback={
              <div
                role="status"
                aria-label="Loading"
                className="grid min-h-dvh place-items-center bg-[var(--color-background)]"
              />
            }
          >
            <RouterProvider router={router} />
          </Suspense>
          <AppToaster />
        </AuthProvider>
      </QueryClientProvider>
    </ThemeProvider>
  );
}

/**
 * Console toaster — a refined card with a per-type tone (left accent stripe +
 * tinted icon chip), display-face title, and a body description lifted toward
 * the foreground so it stays readable on the near-black dark surface. Theme is
 * sourced from the in-app ThemeProvider (not sonner's "system") so the toast
 * tracks the console's own light/dark toggle, not the OS preference. All
 * surface styling lives in globals.css under the `.app-toast` selectors.
 */
function AppToaster() {
  const { theme } = useTheme();
  return (
    <Toaster
      position="top-right"
      closeButton
      theme={theme}
      gap={10}
      expand
      visibleToasts={4}
      icons={{
        success: <CheckCircle2 className="app-toast-glyph" strokeWidth={2.25} />,
        error: <AlertCircle className="app-toast-glyph" strokeWidth={2.25} />,
        warning: <AlertTriangle className="app-toast-glyph" strokeWidth={2.25} />,
        info: <Info className="app-toast-glyph" strokeWidth={2.25} />,
        loading: <Loader2 className="app-toast-glyph app-toast-glyph-spin" strokeWidth={2.25} />,
      }}
      toastOptions={{
        duration: 4200,
        classNames: {
          toast: "app-toast",
          title: "app-toast-title",
          description: "app-toast-description",
          closeButton: "app-toast-close",
          actionButton: "app-toast-action",
          cancelButton: "app-toast-cancel",
        },
      }}
    />
  );
}
