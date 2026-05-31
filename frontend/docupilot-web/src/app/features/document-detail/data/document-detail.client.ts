import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpResponse } from '@angular/common/http';
import { Observable, catchError, map, of, throwError } from 'rxjs';

import { environment } from '@env/environment';

import { AuditLogEntry, DocumentDetail, DocumentTextResponse } from './document-detail.models';
import {
  AgentRecommendAndCreateResponse,
  CreateWorkflowTaskRequest,
  CreatedWorkflowTask,
  WorkflowRecommendation,
} from './workflow-action.models';

/**
 * Outcome of `POST /api/workflows/recommend`, mapped from the FROZEN Phase-8
 * contract (backend DA-054):
 *  - `recommendation` → 200 with the LLM recommendation;
 *  - `'notClassified'` → 409 (the document is not yet classified — the page tells
 *    the user to wait for classification rather than recommending blind);
 *  - `'unavailable'` → 503 (the LLM is starting or down — retryable);
 *  - `'error'` → 400/404/other transport/500 failure.
 *
 * Mirrors the Phase-6/7 discriminated-outcome pattern (`SemanticSearchClient`).
 */
export type RecommendOutcome =
  | { readonly kind: 'recommendation'; readonly recommendation: WorkflowRecommendation }
  | { readonly kind: 'notClassified' }
  | { readonly kind: 'unavailable' }
  | { readonly kind: 'error' };

/**
 * Outcome of `POST /api/workflow-tasks` and `POST /api/agent/recommend-and-create`:
 *  - `created` → 201/200 with the created task (+ recommendation for the agent path);
 *  - `'notClassified'` → 409; `'unavailable'` → 503; `'error'` → other failure.
 */
export type CreateTaskOutcome =
  | {
      readonly kind: 'created';
      readonly task: CreatedWorkflowTask;
      readonly recommendation?: WorkflowRecommendation;
    }
  | { readonly kind: 'notClassified' }
  | { readonly kind: 'unavailable' }
  | { readonly kind: 'error' };

/**
 * Slice-scoped HTTP client for the Document Detail feature.
 *
 * Registered in the route's `providers: []` (NOT `providedIn:'root'`) per the
 * vertical-slice convention (Tech Lead ADR §3.3 / DA-011 §3.3): instantiated
 * only when the `/documents/:id` slice is entered and torn down with it.
 *
 * Thin wrapper over the FROZEN Phase-3 contract (backend DA-024). No error
 * mapping, retry, or caching — the page component owns loading / not-found /
 * error / 409 presentation.
 */
@Injectable()
export class DocumentDetailClient {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${environment.apiBaseUrl}/documents`;
  private readonly recommendUrl = `${environment.apiBaseUrl}/workflows/recommend`;
  private readonly tasksUrl = `${environment.apiBaseUrl}/workflow-tasks`;
  private readonly agentUrl = `${environment.apiBaseUrl}/agent/recommend-and-create`;

  /** `GET /api/documents/{id}` → `DocumentDetail` (404 propagates as an error). */
  getDetail(id: string): Observable<DocumentDetail> {
    return this.http.get<DocumentDetail>(`${this.baseUrl}/${encodeURIComponent(id)}`);
  }

  /**
   * `GET /api/documents/{id}/text` → `DocumentTextResponse`.
   * 404 (text not extracted yet) propagates as an error for the caller to
   * translate into a graceful "Text not available yet" message.
   */
  getText(id: string): Observable<DocumentTextResponse> {
    return this.http.get<DocumentTextResponse>(`${this.baseUrl}/${encodeURIComponent(id)}/text`);
  }

  /** `GET /api/documents/{id}/audit` → `AuditLogEntry[]` (newest-first; `[]` if none). */
  getAudit(id: string): Observable<AuditLogEntry[]> {
    return this.http.get<AuditLogEntry[]>(`${this.baseUrl}/${encodeURIComponent(id)}/audit`);
  }

  /**
   * `POST /api/documents/{id}/process` (manual re-process / retry).
   * Returns the full response so the caller can distinguish `202 Accepted`
   * from `404`/`409 Conflict` (both surface as HTTP errors).
   */
  process(id: string): Observable<HttpResponse<void>> {
    return this.http.post<void>(`${this.baseUrl}/${encodeURIComponent(id)}/process`, null, {
      observe: 'response',
    });
  }

  // --- Phase 8: workflow recommend / create (the document-detail trigger — ADR §7 / Q4) ---

  /**
   * `POST /api/workflows/recommend` { documentId } → the LLM recommendation. The
   * recommend LLM runs on CPU and can take tens of seconds — the caller shows an
   * "Analyzing…" affordance. 409 → not yet classified; 503 → AI unavailable.
   */
  recommendWorkflow(documentId: string): Observable<RecommendOutcome> {
    return this.http
      .post<WorkflowRecommendation>(this.recommendUrl, { documentId })
      .pipe(
        map((recommendation): RecommendOutcome => ({ kind: 'recommendation', recommendation })),
        catchError((err: unknown) => this.mapRecommendError<RecommendOutcome>(err)),
      );
  }

  /**
   * `POST /api/workflow-tasks` { documentId, taskType, assignedTeam, priority,
   * reason? } → 201 the created task (the validated, audited write).
   */
  createTask(request: CreateWorkflowTaskRequest): Observable<CreateTaskOutcome> {
    return this.http.post<CreatedWorkflowTask>(this.tasksUrl, request).pipe(
      map((task): CreateTaskOutcome => ({ kind: 'created', task })),
      catchError((err: unknown) => this.mapRecommendError<CreateTaskOutcome>(err)),
    );
  }

  /**
   * `POST /api/agent/recommend-and-create` { documentId } → the one-click
   * constrained pipeline (recommend → create), returning both the recommendation
   * and the created task. Fails fast (503) at recommend if the LLM is down.
   */
  recommendAndCreate(documentId: string): Observable<CreateTaskOutcome> {
    return this.http
      .post<AgentRecommendAndCreateResponse>(this.agentUrl, { documentId })
      .pipe(
        map(
          (res): CreateTaskOutcome => ({
            kind: 'created',
            task: res.task,
            recommendation: res.recommendation,
          }),
        ),
        catchError((err: unknown) => this.mapRecommendError<CreateTaskOutcome>(err)),
      );
  }

  /**
   * Shared error mapping for the recommend/create/agent calls: 409 → not yet
   * classified, 503 → AI unavailable, everything else → generic error. Returns a
   * single-value Observable of the discriminated outcome so `catchError` can be
   * used inline (mirrors the Phase-6/7 mapping shape).
   */
  private mapRecommendError<T>(err: unknown): Observable<T> {
    if (err instanceof HttpErrorResponse) {
      if (err.status === 409) {
        return of({ kind: 'notClassified' } as T);
      }
      if (err.status === 503) {
        return of({ kind: 'unavailable' } as T);
      }
      return of({ kind: 'error' } as T);
    }
    return throwError(() => err);
  }
}
