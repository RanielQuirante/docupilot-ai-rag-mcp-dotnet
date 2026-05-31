/**
 * TypeScript contracts for the Workflow Tasks slice.
 *
 * These mirror the FROZEN Phase-8 backend contract (Tech Lead ADR
 * "## Phase 8 — MCP-Style Workflow Tools" §6 / backend DA-054). Field names are
 * camelCase on the wire and 1:1 with the Contracts DTOs (`WorkflowTaskDto`,
 * `CreateWorkflowTaskRequest`, `WorkflowRecommendationResponse`,
 * `AgentRecommendAndCreateResponse`). `Priority` and `Status` are enum-name
 * strings on the wire.
 *
 * Endpoints consumed by this slice's client:
 *   - GET  /api/workflow-tasks?status=&documentId=         → WorkflowTaskDto[] (newest-first)
 *   - POST /api/workflow-tasks/{id}/complete               → WorkflowTaskDto (200/404/409)
 *   - POST /api/workflows/recommend                        → WorkflowRecommendationResponse (used by document-detail)
 *   - POST /api/workflow-tasks                             → 201 WorkflowTaskDto (used by document-detail)
 *   - POST /api/agent/recommend-and-create                 → { recommendation, task } (used by document-detail)
 *
 * No `any` anywhere.
 */

/** Closed priority set (backend `WorkflowPriority`). Enum-name strings on the wire. */
export type WorkflowPriority = 'Low' | 'Normal' | 'High';

/** Closed status set (backend `WorkflowTaskStatus`). Enum-name strings on the wire. */
export type WorkflowTaskStatus = 'Open' | 'Completed';

/**
 * One workflow task — 1:1 with the backend `WorkflowTaskDto`. A document can
 * have MANY tasks (1:N). `reason` is nullable; `completedAt` is null while Open.
 */
export interface WorkflowTask {
  /** Server-generated identifier (UUIDv7). */
  readonly id: string;
  /** Source document id (UUIDv7) — links to `/documents/:documentId`. */
  readonly documentId: string;
  /** The recommended workflow / task type, e.g. "LegalReview", "FinanceApproval". Free string. */
  readonly taskType: string;
  /** The assigned team, e.g. "Legal", "Finance". Free string. */
  readonly assignedTeam: string;
  /** Priority enum-name string (`Low` | `Normal` | `High`). */
  readonly priority: string;
  /** Status enum-name string (`Open` | `Completed`). */
  readonly status: string;
  /** The recommendation's justification; may be null. */
  readonly reason: string | null;
  /** ISO-8601 UTC creation timestamp. */
  readonly createdAt: string;
  /** ISO-8601 UTC completion timestamp — null while Open. */
  readonly completedAt: string | null;
}

/** Optional status filter for `GET /api/workflow-tasks` (`undefined` = all). */
export type WorkflowTaskStatusFilter = WorkflowTaskStatus | undefined;

/** Request body for `POST /api/workflows/recommend`. */
export interface RecommendWorkflowRequest {
  readonly documentId: string;
}

/**
 * `200 OK` body for `POST /api/workflows/recommend` — 1:1 with the backend
 * `WorkflowRecommendationResponse`. `priority` is an enum-name string.
 */
export interface WorkflowRecommendation {
  /** The recommended workflow / task type (e.g. "LegalReview"). */
  readonly recommendedWorkflow: string;
  /** The suggested next step (free text). */
  readonly nextStep: string;
  /** Priority enum-name string (`Low` | `Normal` | `High`). */
  readonly priority: string;
  /** The LLM's justification for the recommendation. */
  readonly reason: string;
}

/**
 * Request body for `POST /api/workflow-tasks` (the validated write). Built from
 * a {@link WorkflowRecommendation} on the document-detail "Create task" action.
 */
export interface CreateWorkflowTaskRequest {
  readonly documentId: string;
  readonly taskType: string;
  readonly assignedTeam: string;
  readonly priority: string;
  readonly reason?: string;
}

/** Request body for `POST /api/agent/recommend-and-create`. */
export interface AgentRecommendAndCreateRequest {
  readonly documentId: string;
}

/**
 * `200 OK` body for `POST /api/agent/recommend-and-create` — the constrained
 * one-click recommend→create pipeline. 1:1 with the backend
 * `AgentRecommendAndCreateResponse`.
 */
export interface AgentRecommendAndCreateResponse {
  readonly recommendation: WorkflowRecommendation;
  readonly task: WorkflowTask;
}
