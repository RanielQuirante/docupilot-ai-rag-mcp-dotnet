/**
 * TypeScript contracts for the Document Detail slice.
 *
 * These mirror the FROZEN Phase-3 backend API contract (backend DA-024 /
 * Tech Lead ADR §7):
 *   - GET /api/documents/{id}        → DocumentDetail
 *   - GET /api/documents/{id}/text   → DocumentTextResponse
 *   - GET /api/documents/{id}/audit  → AuditLogEntry[] (newest-first)
 *   - POST /api/documents/{id}/process → 202 / 404 / 409
 *
 * All contracts deliberately OMIT `filePath` (internal storage key — never
 * surfaced to the client). No `any` anywhere.
 */

/**
 * The full document status lifecycle (DA-015 §4.3, extended in Phase 3 with
 * `TextExtracted`). The API stores/returns these as strings.
 */
export type DocumentStatus =
  | 'Uploaded'
  | 'Queued'
  | 'ExtractingText'
  | 'TextExtracted'
  | 'Classifying'
  | 'GeneratingEmbeddings'
  | 'ReadyForSearch'
  | 'Failed';

/** Non-terminal statuses for Phase 3 — while in one of these, the pipeline is still working. */
export const NON_TERMINAL_STATUSES: readonly DocumentStatus[] = ['Queued', 'ExtractingText'];

/** `GET /api/documents/{id}` → 200 `DocumentDetail` / 404. */
export interface DocumentDetail {
  /** Server-generated identifier (UUIDv7). */
  readonly id: string;
  /** Original uploaded file name. */
  readonly fileName: string;
  /** MIME type recorded at upload time. */
  readonly contentType: string;
  /** File size in bytes (rendered human-readable in the UI). */
  readonly sizeBytes: number;
  /** Current processing status. */
  readonly status: string;
  /** ISO-8601 UTC upload timestamp. */
  readonly uploadedAt: string;
  /** ISO-8601 UTC processing-finished timestamp — set when terminal (TextExtracted/Failed). */
  readonly processedAt: string | null;
  /** Short human-readable failure reason — non-null only when `status === 'Failed'`. */
  readonly failureReason: string | null;
  /** Length of extracted text — null until text exists. */
  readonly charCount: number | null;
  /** ISO-8601 UTC extraction timestamp — null until text exists. */
  readonly extractedAt: string | null;
}

/** `GET /api/documents/{id}/text` → 200 `DocumentTextResponse` / 404 (no text yet). */
export interface DocumentTextResponse {
  readonly documentId: string;
  /** Full extracted plain text (the LOB — fetched only on demand). */
  readonly content: string;
  readonly charCount: number;
  readonly extractedAt: string;
}

/** Known audit actions (DA-024). Rendered human-readable; unknown values pass through. */
export type AuditAction =
  | 'Queued'
  | 'ExtractionStarted'
  | 'ExtractionSucceeded'
  | 'ExtractionFailed'
  | 'ReprocessQueued';

/** One entry of `GET /api/documents/{id}/audit` (returned newest-first). */
export interface AuditLogEntry {
  readonly id: string;
  /** Event name, e.g. `ExtractionSucceeded`. */
  readonly action: string;
  /** A JSON **string** (parse client-side) describing the event; may be null. */
  readonly detailsJson: string | null;
  /** ISO-8601 UTC event timestamp. */
  readonly createdAt: string;
}
