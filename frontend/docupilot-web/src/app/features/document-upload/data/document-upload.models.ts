/**
 * TypeScript mirror of the FROZEN backend upload contract
 * (`POST /api/documents/upload` — ADR §3.1, backend DA-016).
 *
 * The endpoint accepts `multipart/form-data` with a repeatable field named
 * `files` and ALWAYS returns a body of shape `{ uploaded: [], failed: [] }`
 * (201 if >= 1 file stored, 400 if the whole request is empty/all-invalid).
 */

/** A single successfully-persisted document (an entry in `uploaded[]`). */
export interface UploadedDocument {
  readonly id: string;
  readonly fileName: string;
  readonly contentType: string;
  readonly sizeBytes: number;
  readonly status: string;
  readonly uploadedAt: string;
}

/** A single rejected file (an entry in `failed[]`). */
export interface FailedDocument {
  readonly fileName: string;
  readonly error: string;
}

/** The body returned by the upload endpoint (always present, even on 400). */
export interface UploadDocumentResponse {
  readonly uploaded: readonly UploadedDocument[];
  readonly failed: readonly FailedDocument[];
}
