/**
 * TypeScript contracts for the Document Library slice.
 *
 * These mirror the FROZEN backend API contract for `GET /api/documents`
 * (Tech Lead ADR §3.2 / backend DA-016, extended in Phase 3 / DA-024).
 * `DocumentListItem` deliberately omits `filePath` (internal storage key —
 * never surfaced to the client). As of Phase 4 (DA-032) each row carries a
 * nullable `classification` category string (confidence/metadata remain
 * detail-only); it is `null` until the document reaches `Classified`.
 */

/**
 * The processing-status state machine surfaced by the Phase-3 contract
 * (DA-024) and extended for Phase-4 classification (DA-032). Non-terminal
 * states (`Uploaded`/`Queued`/`ExtractingText`/`TextExtracted`/`Classifying`)
 * drive the library's live poll; `Classified`/`ReadyForSearch`/`Failed` are
 * terminal for the current pipeline. Kept as a string-literal union so the
 * badge map stays exhaustive while still tolerating an unknown server value.
 */
export type DocumentStatus =
  | 'Uploaded'
  | 'Queued'
  | 'ExtractingText'
  | 'TextExtracted'
  | 'Classifying'
  | 'Classified'
  | 'GeneratingEmbeddings'
  | 'ReadyForSearch'
  | 'Failed';

/**
 * Statuses for which the pipeline is still expected to advance on its own.
 * While any visible row is in one of these, the page polls `GET /api/documents`.
 *
 * Phase-4 (DA-032/DA-035): `TextExtracted` and `Classifying` are now
 * non-terminal — a `TextExtracted` doc is still awaiting classification, and
 * `Classifying` is mid-LLM, so the live poll must keep running through both so
 * the category chip appears as soon as the row reaches `Classified`.
 */
export const NON_TERMINAL_STATUSES: ReadonlySet<string> = new Set<DocumentStatus>([
  'Uploaded',
  'Queued',
  'ExtractingText',
  'TextExtracted',
  'Classifying',
]);

/** True when the document's status is still expected to advance automatically. */
export function isNonTerminalStatus(status: string): boolean {
  return NON_TERMINAL_STATUSES.has(status);
}

/** One row of the paginated document list (`GET /api/documents` → `items[]`). */
export interface DocumentListItem {
  /** Server-generated identifier (UUIDv7). */
  readonly id: string;
  /** Original uploaded file name. */
  readonly fileName: string;
  /** MIME type recorded at upload time. */
  readonly contentType: string;
  /** File size in bytes (rendered human-readable in the UI). */
  readonly sizeBytes: number;
  /** Processing status (Phase-3 state machine — see {@link DocumentStatus}). */
  readonly status: string;
  /** ISO-8601 UTC upload timestamp. */
  readonly uploadedAt: string;
  /** ISO-8601 UTC processing timestamp — `null` until a terminal state. */
  readonly processedAt: string | null;
  /** Populated only when `status === 'Failed'` (Phase-3 / DA-024). */
  readonly failureReason?: string | null;
  /**
   * AI-assigned category (Phase-4 / DA-032) — one of the 8-value taxonomy as
   * the spec display string (e.g. `'Invoice'`, `'Employee Record'`,
   * `'Unknown'`). `null`/absent until the document is `Classified`. The list
   * projection carries the category string only; confidence/metadata are
   * detail-only.
   */
  readonly classification?: string | null;
}

/**
 * Generic paged-result envelope returned by list endpoints.
 * Mirrors `PagedResult<T>` from the backend contract.
 */
export interface PagedResult<T> {
  readonly items: readonly T[];
  readonly page: number;
  readonly pageSize: number;
  readonly totalCount: number;
  readonly totalPages: number;
}
