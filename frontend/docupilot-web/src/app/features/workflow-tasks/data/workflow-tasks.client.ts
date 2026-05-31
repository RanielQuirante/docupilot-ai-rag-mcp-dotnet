import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Observable, catchError, map, of, throwError } from 'rxjs';

import { environment } from '@env/environment';

import { WorkflowTask, WorkflowTaskStatusFilter } from './workflow-tasks.models';

/**
 * Outcome of `GET /api/workflow-tasks`, mapped from the FROZEN Phase-8 contract
 * (backend DA-054):
 *  - `tasks` → 200 with the (possibly empty) newest-first task list;
 *  - `'unavailable'` → 503 (a dependent service starting or down) — a clean,
 *    retryable signal the page surfaces distinctly from a generic error;
 *  - `'error'` → any other transport/server failure (incl. an unexpected 500).
 *
 * Mirrors the Phase-6 `SemanticSearchClient.SearchOutcome` / Phase-7
 * `AskAiClient.AskOutcome` discriminated-outcome pattern.
 */
export type ListTasksOutcome =
  | { readonly kind: 'tasks'; readonly tasks: readonly WorkflowTask[] }
  | { readonly kind: 'unavailable' }
  | { readonly kind: 'error' };

/**
 * Outcome of `POST /api/workflow-tasks/{id}/complete`:
 *  - `completed` → 200 with the updated task (Status=Completed, CompletedAt set);
 *  - `'alreadyCompleted'` → 409 (the task was already completed — the page
 *    treats this as benign and refetches the list to reconcile);
 *  - `'notFound'` → 404 (the task no longer exists — refetch);
 *  - `'unavailable'` → 503; `'error'` → any other failure.
 */
export type CompleteTaskOutcome =
  | { readonly kind: 'completed'; readonly task: WorkflowTask }
  | { readonly kind: 'alreadyCompleted' }
  | { readonly kind: 'notFound' }
  | { readonly kind: 'unavailable' }
  | { readonly kind: 'error' };

/**
 * Slice-scoped HTTP client for the Workflow Tasks page.
 *
 * Registered in the route's `providers: []` (NOT `providedIn:'root'`) per the
 * vertical-slice convention (Tech Lead ADR §3.3 / DA-011 §3.3): created when
 * the `/tasks` slice is entered and torn down with it.
 *
 * It maps the contract's status codes to discriminated outcomes (mirroring
 * `SemanticSearchClient`) so the page can render its states without bespoke
 * error plumbing. The recommend/create endpoints live on the document-detail
 * slice (where the trigger UI lives — ADR §7 / Q4), not here.
 */
@Injectable()
export class WorkflowTasksClient {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${environment.apiBaseUrl}/workflow-tasks`;

  /**
   * `GET /api/workflow-tasks?status=&documentId=` → `WorkflowTaskDto[]`
   * (newest-first). Both filters optional.
   */
  listTasks(status?: WorkflowTaskStatusFilter, documentId?: string): Observable<ListTasksOutcome> {
    let params = new HttpParams();
    if (status) {
      params = params.set('status', status);
    }
    if (documentId) {
      params = params.set('documentId', documentId);
    }
    return this.http.get<WorkflowTask[]>(this.baseUrl, { params }).pipe(
      map((tasks): ListTasksOutcome => ({ kind: 'tasks', tasks })),
      catchError((err: unknown) => {
        if (err instanceof HttpErrorResponse) {
          if (err.status === 503) {
            return of<ListTasksOutcome>({ kind: 'unavailable' });
          }
          return of<ListTasksOutcome>({ kind: 'error' });
        }
        return throwError(() => err);
      }),
    );
  }

  /**
   * `POST /api/workflow-tasks/{id}/complete` → 200 updated `WorkflowTaskDto`.
   * 409 (already completed) and 404 (missing) are mapped to benign outcomes the
   * page reconciles with a refetch.
   */
  completeTask(id: string): Observable<CompleteTaskOutcome> {
    const url = `${this.baseUrl}/${encodeURIComponent(id)}/complete`;
    return this.http.post<WorkflowTask>(url, null).pipe(
      map((task): CompleteTaskOutcome => ({ kind: 'completed', task })),
      catchError((err: unknown) => {
        if (err instanceof HttpErrorResponse) {
          if (err.status === 409) {
            return of<CompleteTaskOutcome>({ kind: 'alreadyCompleted' });
          }
          if (err.status === 404) {
            return of<CompleteTaskOutcome>({ kind: 'notFound' });
          }
          if (err.status === 503) {
            return of<CompleteTaskOutcome>({ kind: 'unavailable' });
          }
          return of<CompleteTaskOutcome>({ kind: 'error' });
        }
        return throwError(() => err);
      }),
    );
  }
}
