import { api, unwrap, unwrapVoid, type Paged, type Schemas } from "@/lib/api-client";

export type VisibilityValue = Schemas["Visibility"];
export type FileAssetStatusValue = Schemas["FileAssetStatus"];
export type FileAssetDto = Schemas["FileAssetDto"];
export type PresignedUploadResponse = Schemas["PresignedUploadResponse"];
export type PresignedDownloadResponse = Schemas["PresignedDownloadResponse"];
export type RequestUploadUrlInput = Schemas["RequestUploadUrlCommand"];

/** Value mirrors of the generated enums, for use where a *value* is needed. */
export const Visibility = {
  Public: "Public",
  Private: "Private",
} as const satisfies Record<string, VisibilityValue>;

export const FileAssetStatus = {
  PendingUpload: "PendingUpload",
  Available: "Available",
  Quarantined: "Quarantined",
} as const satisfies Record<string, FileAssetStatusValue>;

export async function requestUploadUrl(
  input: RequestUploadUrlInput,
): Promise<PresignedUploadResponse> {
  return unwrap(await api.POST("/api/v1/files/upload-url", { body: input }));
}

export async function finalizeUpload(fileAssetId: string): Promise<FileAssetDto> {
  return unwrap(
    await api.POST("/api/v1/files/{id}/finalize", { params: { path: { id: fileAssetId } } }),
  );
}

export async function getFileMetadata(fileAssetId: string): Promise<FileAssetDto> {
  return unwrap(await api.GET("/api/v1/files/{id}", { params: { path: { id: fileAssetId } } }));
}

export async function getFileDownloadUrl(
  fileAssetId: string,
  options: { inline?: boolean } = {},
): Promise<PresignedDownloadResponse> {
  return unwrap(
    await api.GET("/api/v1/files/{id}/url", {
      params: { path: { id: fileAssetId }, query: { inline: options.inline } },
    }),
  );
}

export async function listMyFiles(page = 1, pageSize = 20): Promise<FileAssetDto[]> {
  return unwrap(await api.GET("/api/v1/files/mine", { params: { query: { page, pageSize } } }));
}

export async function listSharedFiles(page = 1, pageSize = 20): Promise<FileAssetDto[]> {
  return unwrap(await api.GET("/api/v1/files/shared", { params: { query: { page, pageSize } } }));
}

/**
 * Flip a file's visibility. The server returns the refreshed DTO so the client can patch
 * its preview/list without a follow-up GET.
 */
export async function changeFileVisibility(
  fileAssetId: string,
  visibility: VisibilityValue,
): Promise<FileAssetDto> {
  return unwrap(
    await api.PATCH("/api/v1/files/{id}/visibility", {
      params: { path: { id: fileAssetId } },
      body: { visibility },
    }),
  );
}

export async function deleteFile(fileAssetId: string): Promise<void> {
  unwrapVoid(await api.DELETE("/api/v1/files/{id}", { params: { path: { id: fileAssetId } } }));
}

export async function listTrashedFiles(
  pageNumber = 1,
  pageSize = 20,
): Promise<Paged<FileAssetDto>> {
  return unwrap(
    await api.GET("/api/v1/files/trash", { params: { query: { pageNumber, pageSize } } }),
  );
}

export async function restoreFile(fileAssetId: string): Promise<void> {
  unwrapVoid(
    await api.POST("/api/v1/files/{id}/restore", { params: { path: { id: fileAssetId } } }),
  );
}
