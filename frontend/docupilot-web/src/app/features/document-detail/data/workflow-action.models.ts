/**
 * TypeScript contracts for the document-detail "Recommend workflow / Create
 * task" action (Phase 8, ADR §7 / Q4 — the recommend/create trigger lives where
 * the user is viewing the document).
 *
 * These mirror the FROZEN Phase-8 backend contract (backend DA-054):
 *   - POST /api/workflows/recommend         { documentId }  → WorkflowRecommendation (200/400/404/409/503)
 *   - POST /api/workflow-tasks              { documentId, taskType, assignedTeam, priority, reason? } → 201 CreatedWorkflowTask
 *   - POST /api/agent/recommend-and-create  { documentId }  → { recommendation, task } (200/.../503)
 *
 * Kept slice-local (the `workflow-tasks` slice owns its own copies) so the two
 * slices stay independently scoped. `priority`/`status` are enum-name strings.
 */

/** `200 OK` body for `POST /api/workflows/recommend`. */
export interface WorkflowRecommendation {
  readonly recommendedWorkflow: string;
  readonly nextStep: string;
  /** Enum-name string (`Low` | `Normal` | `High`). */
  readonly priority: string;
  readonly reason: string;
}

/** Request body for `POST /api/workflow-tasks` (the validated write). */
export interface CreateWorkflowTaskRequest {
  readonly documentId: string;
  readonly taskType: string;
  readonly assignedTeam: string;
  readonly priority: string;
  readonly reason?: string;
}

/** `201 Created` body for `POST /api/workflow-tasks` — the persisted task (subset used here). */
export interface CreatedWorkflowTask {
  readonly id: string;
  readonly documentId: string;
  readonly taskType: string;
  readonly assignedTeam: string;
  readonly priority: string;
  readonly status: string;
  readonly reason: string | null;
  readonly createdAt: string;
  readonly completedAt: string | null;
}

/** `200 OK` body for `POST /api/agent/recommend-and-create`. */
export interface AgentRecommendAndCreateResponse {
  readonly recommendation: WorkflowRecommendation;
  readonly task: CreatedWorkflowTask;
}
