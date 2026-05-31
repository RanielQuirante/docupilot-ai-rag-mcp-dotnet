/**
 * TypeScript contracts for the Dashboard slice.
 *
 * These mirror the FROZEN backend contract for `GET /api/dashboard/stats`
 * (Tech Lead ADR "## Phase 9" §11.1 / backend DA-058). Field names are
 * camelCase on the wire and 1:1 with the Contracts DTOs (`DashboardStats`,
 * `ClassificationBreakdownItem`). The endpoint always returns `200` — an empty
 * database yields all-zero counts and an empty `classificationBreakdown`.
 *
 * No `any` anywhere.
 */

/**
 * One row of the classification breakdown — 1:1 with the backend
 * `ClassificationBreakdownItem`. `category` is the spec DISPLAY string
 * (e.g. `"Employee Record"`), already mapped server-side. Only categories with
 * ≥1 classified document appear; the list is ordered count DESC, then category.
 */
export interface ClassificationBreakdownItem {
  /** The spec display string for the category (e.g. `"Invoice"`). */
  readonly category: string;
  /** Number of classified documents in this category (≥1). */
  readonly count: number;
}

/**
 * Aggregate dashboard metrics — 1:1 with the backend `DashboardStats`.
 *
 * `pendingProcessing` is the union of the six in-flight, non-terminal
 * processing statuses (Queued + ExtractingText + TextExtracted + Classifying +
 * Classified + GeneratingEmbeddings); it EXCLUDES `Uploaded`, `ReadyForSearch`
 * and `Failed` (their own buckets). `totalDocuments` is the sum of ALL status
 * buckets including `Uploaded`.
 */
export interface DashboardStats {
  /** Total documents across every status bucket (incl. `Uploaded`). */
  readonly totalDocuments: number;
  /** Documents in one of the six in-flight processing statuses. */
  readonly pendingProcessing: number;
  /** Documents whose status is `ReadyForSearch`. */
  readonly readyForSearch: number;
  /** Documents whose status is `Failed`. */
  readonly failed: number;
  /** Open workflow tasks (`WorkflowTasks WHERE Status == Open`). */
  readonly pendingWorkflowTasks: number;
  /** Per-category classified-document counts (count DESC; may be empty). */
  readonly classificationBreakdown: readonly ClassificationBreakdownItem[];
}
