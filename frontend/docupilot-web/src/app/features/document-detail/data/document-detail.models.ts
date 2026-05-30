/**
 * TypeScript contracts for the Document Detail slice.
 *
 * These mirror the FROZEN backend API contract (Phase-3 DA-024, extended
 * additively by Phase-4 DA-032 with nullable `classification` + parsed
 * `metadata` on the detail DTO):
 *   - GET /api/documents/{id}        → DocumentDetail (+classification, +metadata)
 *   - GET /api/documents/{id}/text   → DocumentTextResponse
 *   - GET /api/documents/{id}/audit  → AuditLogEntry[] (newest-first)
 *   - POST /api/documents/{id}/process → 202 / 404 / 409
 *
 * The detail call already returns both classification + metadata, so the slice
 * consumes the single `GET /{id}` payload (DA-034) — the standalone
 * `GET /{id}/classification` + `/{id}/metadata` sub-endpoints are not needed.
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

/**
 * Non-terminal statuses — while in one of these, the pipeline is still working
 * and the page polls for live updates. Phase 4 adds `Classifying` so the UI
 * auto-advances `TextExtracted → Classifying → Classified` without a refresh.
 */
export const NON_TERMINAL_STATUSES: readonly DocumentStatus[] = [
  'Queued',
  'ExtractingText',
  'Classifying',
];

/**
 * The 8-value classification taxonomy (spec §5.3 display form, FROZEN by backend
 * DA-032). Display strings may contain spaces. `Unknown` is the ambiguous/off-
 * taxonomy fallback. The API returns these verbatim as the `category` string.
 */
export type DocumentCategory =
  | 'Contract'
  | 'Invoice'
  | 'Employee Record'
  | 'Legal Document'
  | 'Compliance Document'
  | 'Client Correspondence'
  | 'Policy Document'
  | 'Unknown';

/**
 * LLM classification result — embedded in the detail DTO (`classification`) and
 * also available standalone at `GET /{id}/classification`. Null on the detail
 * payload until the document reaches `Classified` (backend DA-032).
 */
export interface DocumentClassification {
  /** One of the 8 taxonomy categories (string; spaces allowed). */
  readonly category: string;
  /** Model confidence in `[0, 1]` (rendered as a percentage / bar). */
  readonly confidence: number;
  /** Short human-readable rationale for the assigned category. */
  readonly reason: string;
  /** The LLM model that produced the classification (e.g. `llama3.2:3b`). */
  readonly model: string;
  /** ISO-8601 UTC timestamp when the classification was produced. */
  readonly createdAt: string;
}

/**
 * Schemaless extracted metadata — the PARSED JSON object the backend returns on
 * the detail DTO (`metadata`). Keys/shape are NOT fixed (the LLM decides). Empty
 * extraction is `{}`; absent (not yet classified) is `null`. Values may be any
 * JSON type, so the UI renders them generically.
 */
export type DocumentMetadata = Readonly<Record<string, unknown>>;

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
  /**
   * LLM classification — null until the document is `Classified` (Phase 4,
   * backend DA-032). An LLM-down document stays `TextExtracted` with this null.
   */
  readonly classification: DocumentClassification | null;
  /**
   * Parsed extracted-metadata object — null until classified; `{}` when the LLM
   * extracted no fields. Schemaless (iterate keys; do NOT assume fixed fields).
   */
  readonly metadata: DocumentMetadata | null;
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
  | 'ClassificationStarted'
  | 'ClassificationSucceeded'
  | 'ClassificationFailed'
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
