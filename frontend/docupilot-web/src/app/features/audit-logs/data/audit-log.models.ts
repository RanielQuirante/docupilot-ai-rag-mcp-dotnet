/**
 * TypeScript contracts for the Audit Logs slice.
 *
 * These mirror the FROZEN backend contract for
 * `GET /api/audit-logs?page=&pageSize=&entityId=&action=` (Tech Lead ADR
 * "## Phase 9" §11.8 / backend DA-058). Field names are camelCase on the wire
 * and 1:1 with the Contracts DTO `AuditLogListItem` wrapped in the existing
 * `PagedResult<T>` envelope (the same shape `GET /api/documents` returns).
 *
 * No `any` anywhere.
 */

/**
 * Generic paged-result envelope returned by list endpoints — 1:1 with the
 * backend `PagedResult<T>`. Re-declared here (rather than imported from the
 * document-library slice) to keep this vertical slice self-contained.
 */
export interface PagedResult<T> {
  readonly items: readonly T[];
  readonly page: number;
  readonly pageSize: number;
  readonly totalCount: number;
  readonly totalPages: number;
}

/**
 * One audit-log entry — 1:1 with the backend `AuditLogListItem`. The global
 * timeline spans multiple entity kinds (`Document` / `WorkflowTask` /
 * `WorkflowTool`), so unlike the per-document `AuditLogEntry` it carries both
 * `entityName` and `entityId`. `detailsJson` is a nullable raw JSON string
 * (rendered readably in the UI, never dumped raw).
 */
export interface AuditLogListItem {
  /** Server-generated identifier (UUIDv7). */
  readonly id: string;
  /** The audited entity kind: `Document` | `WorkflowTask` | `WorkflowTool`. */
  readonly entityName: string;
  /** The audited entity id — links to `/documents/:id` when `entityName === 'Document'`. */
  readonly entityId: string;
  /** The audit action enum-name string (see {@link AUDIT_ACTIONS}). */
  readonly action: string;
  /** Raw JSON details payload — nullable; pretty-printed/summarised in the UI. */
  readonly detailsJson: string | null;
  /** ISO-8601 UTC timestamp the entry was recorded. */
  readonly createdAt: string;
}

/**
 * The closed set of `AuditAction` enum names the backend persists (DA-024 /
 * DA-032 / DA-039 / DA-057). Grouped by pipeline stage for the filter dropdown.
 * The AI-tool actions (`ToolInvoked`/`ToolSucceeded`/`ToolFailed`) are the
 * Phase-8 "every AI action is audited" entries and are surfaced prominently.
 *
 * Only valid enum names are ever sent as the `action` query param — an invalid
 * action would yield a 400, which this UI never triggers.
 */
export const AUDIT_ACTIONS = [
  // Phase 3 — text-extraction pipeline
  'Queued',
  'ExtractionStarted',
  'ExtractionSucceeded',
  'ExtractionFailed',
  'ReprocessQueued',
  // Phase 4 — classification
  'ClassificationStarted',
  'ClassificationSucceeded',
  'ClassificationFailed',
  // Phase 5 — embeddings
  'EmbeddingStarted',
  'EmbeddingSucceeded',
  'EmbeddingFailed',
  // Phase 8 — AI workflow tools (the safety-story entries)
  'ToolInvoked',
  'ToolSucceeded',
  'ToolFailed',
] as const;

/** A valid `action` filter value, or `undefined` for the unfiltered timeline. */
export type AuditActionFilter = (typeof AUDIT_ACTIONS)[number] | undefined;
