import { useState, type FormEvent } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Camera, Fingerprint, UserCircle2 } from "lucide-react";
import { toast } from "sonner";
import { useAuth } from "@/auth/use-auth";
import {
  getMyProfile,
  isProfileConflict,
  updateMyProfile,
  type MyProfile,
} from "@/api/identity";
import { ApiRequestError } from "@/lib/api-client";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { ImageInput, type ImageUpload } from "@/components/file/image-input";
import { SettingsSection } from "@/pages/settings/settings-layout";

const PROFILE_KEY = ["identity", "me"] as const;

export function ProfileSettings() {
  const { user } = useAuth();
  const queryClient = useQueryClient();

  const profileQuery = useQuery({
    queryKey: PROFILE_KEY,
    queryFn: getMyProfile,
  });

  const profile = profileQuery.data;
  const loading = profileQuery.isLoading;
  // Before the profile arrives, the JWT-derived `user` gives an immediate
  // non-empty paint.
  const provisional = !profile && user && loading ? user.name?.split(" ") : undefined;
  const [firstName, setFirstName] = useState(
    () => (profile ? profile.firstName : provisional?.[0]) ?? "",
  );
  const [lastName, setLastName] = useState(
    () => (profile ? profile.lastName : provisional?.slice(1).join(" ")) ?? "",
  );
  const [phone, setPhone] = useState(() => profile?.phoneNumber ?? "");
  // Set when a save was refused because the profile changed elsewhere (412). Cleared by the next
  // successful save.
  const [conflict, setConflict] = useState(false);

  // Seed the form from the fetched profile exactly once, during render; the
  // authoritative profile then locks. Seeding once means a later background
  // refetch can't clobber edits the user has already made.
  const [seeded, setSeeded] = useState(profile !== undefined);
  if (!seeded && profile) {
    setSeeded(true);
    setFirstName(profile.firstName ?? "");
    setLastName(profile.lastName ?? "");
    setPhone(profile.phoneNumber ?? "");
  }

  /**
   * A save refused with 412: someone changed the profile after this page loaded it (#107). Refetch
   * it, put the latest values in the form and say so. Never resend: the form's values for every
   * field the user did not touch are the old ones, and sending them again would overwrite exactly
   * the change the 412 protected.
   */
  const showConflict = async () => {
    setConflict(true);
    try {
      const latest = await queryClient.fetchQuery({
        queryKey: PROFILE_KEY,
        queryFn: getMyProfile,
        staleTime: 0,
      });
      setFirstName(latest.firstName ?? "");
      setLastName(latest.lastName ?? "");
      setPhone(latest.phoneNumber ?? "");
    } catch {
      toast.error("Couldn't load the latest profile", { description: "Reload the page to see it." });
    }
  };

  // Every save carries the profile the user was shown (and so its version), passed through
  // `mutate(arg)` rather than read from a closure.
  const saveMutation = useMutation({
    mutationFn: (base: MyProfile) =>
      updateMyProfile(base, {
        firstName: firstName.trim() || null,
        lastName: lastName.trim() || null,
        phoneNumber: phone.trim() || null,
      }),
    retry: false,
    onSuccess: () => {
      setConflict(false);
      toast.success("Profile saved");
      queryClient.invalidateQueries({ queryKey: PROFILE_KEY });
    },
    onError: (err: unknown) => {
      if (isProfileConflict(err)) {
        void showConflict();
        return;
      }
      const message =
        err instanceof ApiRequestError
          ? err.problem?.detail ?? err.problem?.title ?? err.message
          : "Failed to save profile";
      toast.error("Save failed", { description: message });
    },
  });

  const onSubmit = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (profile) saveMutation.mutate(profile);
  };

  const onReset = () => {
    if (profile) {
      setFirstName(profile.firstName ?? "");
      setLastName(profile.lastName ?? "");
      setPhone(profile.phoneNumber ?? "");
    }
  };

  const saving = saveMutation.isPending;
  const dirty =
    !!profile &&
    ((profile.firstName ?? "") !== firstName ||
      (profile.lastName ?? "") !== lastName ||
      (profile.phoneNumber ?? "") !== phone);

  const imageError = (e: unknown) => {
    if (isProfileConflict(e)) {
      void showConflict();
      return;
    }
    const message =
      e instanceof ApiRequestError
        ? (e.problem?.detail ?? e.problem?.title ?? e.message)
        : "Failed to update profile image";
    toast.error(message);
  };

  /**
   * The avatar rides on the profile PUT as raw bytes. The server writes it with
   * `IStorageService.UploadAsync` into the `uploads/` prefix — public-read by design — and stores
   * the durable unsigned URL it returns. It deliberately does NOT go through the Files module,
   * whose `publicUrl` is a presigned GET that expires in minutes (issue #72).
   */
  const uploadMutation = useMutation({
    mutationFn: ({ base, image }: { base: MyProfile; image: ImageUpload }) =>
      updateMyProfile(base, { image }),
    retry: false,
    onSuccess: () => {
      setConflict(false);
      toast.success("Profile image updated");
      queryClient.invalidateQueries({ queryKey: PROFILE_KEY });
    },
    onError: imageError,
  });

  /** Clearing also deletes the stored object, so an orphan does not linger in the bucket. */
  const clearMutation = useMutation({
    mutationFn: (base: MyProfile) => updateMyProfile(base, { deleteCurrentImage: true }),
    retry: false,
    onSuccess: () => {
      setConflict(false);
      toast.success("Profile image removed");
      queryClient.invalidateQueries({ queryKey: PROFILE_KEY });
    },
    onError: imageError,
  });

  return (
    <form onSubmit={onSubmit} className="space-y-5 app-enter">
      {profileQuery.isError && (
        <div
          role="alert"
          className="flex items-start gap-2 rounded-lg border border-[oklch(from_var(--color-destructive)_l_c_h_/_0.30)] bg-[oklch(from_var(--color-destructive)_l_c_h_/_0.06)] px-3 py-2 text-[13px] text-[var(--color-destructive)]"
        >
          <span>
            Couldn't load your profile, so it can't be saved right now. Showing
            details from your session; reload the page to try again.
          </span>
        </div>
      )}
      {conflict && (
        <div
          role="alert"
          className="flex items-start gap-2 rounded-lg border border-[oklch(from_var(--color-destructive)_l_c_h_/_0.30)] bg-[oklch(from_var(--color-destructive)_l_c_h_/_0.06)] px-3 py-2 text-[13px] text-[var(--color-destructive)]"
        >
          <span>
            Your profile was changed somewhere else — another tab or device — after this page
            loaded it, so your change was not saved. The latest version is shown below; make your
            change again if you still want it.
          </span>
        </div>
      )}
      <SettingsSection
        title="Photo"
        icon={Camera}
        description="Shown in the topbar and on your activity. Square crops work best — upload a JPG, PNG or ICO; the server stores it and hands back its address."
      >
        <ImageInput
          value={profile?.imageUrl ?? ""}
          onUpload={(image) => {
            if (profile) uploadMutation.mutate({ base: profile, image });
          }}
          onRemove={() => {
            if (profile) clearMutation.mutate(profile);
          }}
          busy={!profile || uploadMutation.isPending || clearMutation.isPending}
          shape="circle"
        />
      </SettingsSection>

      <SettingsSection
        title="Identity"
        icon={UserCircle2}
        description="Your name and contact details, visible across the dashboard."
        footer={
          <div className="flex items-center justify-end gap-2">
            <Button
              type="button"
              variant="ghost"
              onClick={onReset}
              disabled={saving || !dirty}
              size="sm"
            >
              Reset
            </Button>
            <Button type="submit" disabled={saving || !dirty} size="sm">
              {saving ? "Saving…" : "Save changes"}
            </Button>
          </div>
        }
      >
        <div className="grid gap-5 sm:grid-cols-2">
          <Field id="first-name" label="First name">
            <Input
              id="first-name"
              value={firstName}
              onChange={(e) => setFirstName(e.target.value)}
              autoComplete="given-name"
              disabled={loading}
              className="h-10 text-[13px]"
            />
          </Field>
          <Field id="last-name" label="Last name">
            <Input
              id="last-name"
              value={lastName}
              onChange={(e) => setLastName(e.target.value)}
              autoComplete="family-name"
              disabled={loading}
              className="h-10 text-[13px]"
            />
          </Field>
          <Field id="email" label="Email">
            <Input
              id="email"
              type="email"
              value={profile?.email ?? user?.email ?? ""}
              readOnly
              disabled
              className="h-10 cursor-not-allowed bg-[var(--color-muted)] text-[13px]"
            />
            <p className="mt-1 text-[11px] text-[var(--color-muted-foreground)]">
              Contact your tenant admin to change your sign-in email.
            </p>
          </Field>
          <Field id="phone" label="Phone">
            <Input
              id="phone"
              type="tel"
              value={phone}
              onChange={(e) => setPhone(e.target.value)}
              autoComplete="tel"
              placeholder="+1 (555) 123-4567"
              disabled={loading}
              className="h-10 text-[13px]"
            />
          </Field>
        </div>
      </SettingsSection>

      <SettingsSection
        title="Subject identifier"
        icon={Fingerprint}
        description="The unique ID this account uses inside the platform. Read-only."
      >
        <code className="block w-full overflow-x-auto rounded-lg border border-[var(--color-border)] bg-[var(--color-muted)] px-3 py-2 font-mono text-xs">
          {profile?.id ?? user?.id ?? "—"}
        </code>
      </SettingsSection>
    </form>
  );
}

function Field({
  id,
  label,
  children,
}: {
  id: string;
  label: string;
  children: React.ReactNode;
}) {
  return (
    <div>
      <Label
        htmlFor={id}
        className="mb-1.5 block text-[11.5px] font-semibold uppercase tracking-wider text-[var(--color-muted-foreground)]"
      >
        {label}
      </Label>
      {children}
    </div>
  );
}
