/**
 * TypeScript contracts for the Document Library slice.
 *
 * These mirror the FROZEN backend API contract for `GET /api/documents`
 * (Tech Lead ADR §3.2 / backend DA-016). `DocumentListItem` deliberately
 * omits `filePath` (internal storage key — never surfaced to the client).
 * Classification/Confidence are absent from the contract in Phase 2 (no
 * classification until Phase 4) — the UI renders them as "Pending"/"—".
 */

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
  /** Processing status — always `Uploaded` in Phase 2. */
  readonly status: string;
  /** ISO-8601 UTC upload timestamp. */
  readonly uploadedAt: string;
  /** ISO-8601 UTC processing timestamp — `null` in Phase 2. */
  readonly processedAt: string | null;
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
