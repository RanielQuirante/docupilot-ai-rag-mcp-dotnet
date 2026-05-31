import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Observable, catchError, map, of, throwError } from 'rxjs';

import { environment } from '@env/environment';

import { AuditActionFilter, AuditLogListItem, PagedResult } from './audit-log.models';

/**
 * Outcome of `GET /api/audit-logs`, mapped from the FROZEN Phase-9 contract
 * (backend DA-058):
 *  - `page` → 200 with the (possibly empty) newest-first page of entries;
 *  - `'error'` → any transport/server failure. (A `400` only occurs for an
 *    invalid `action`, which this UI never sends — it offers only valid enum
 *    names — so it is folded into the generic error.)
 *
 * Mirrors the discriminated-outcome pattern of `WorkflowTasksClient`.
 */
export type ListAuditLogsOutcome =
  | { readonly kind: 'page'; readonly result: PagedResult<AuditLogListItem> }
  | { readonly kind: 'error' };

/**
 * Slice-scoped HTTP client for the Audit Logs page.
 *
 * Registered in the route's `providers: []` (NOT `providedIn:'root'`) per the
 * vertical-slice convention (Tech Lead ADR §3.3 / DA-011 §3.3): created when
 * the `/audit` slice is entered and torn down with it.
 */
@Injectable()
export class AuditLogsClient {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${environment.apiBaseUrl}/audit-logs`;

  /**
   * `GET /api/audit-logs?page=&pageSize=&action=` → `PagedResult<AuditLogListItem>`
   * (newest-first). The `action` filter is optional; only valid enum names are
   * ever passed.
   *
   * @param page     1-based page index.
   * @param pageSize Items per page (server caps at 100; default 50).
   * @param action   Optional action filter (`undefined` = unfiltered timeline).
   */
  list(page: number, pageSize: number, action?: AuditActionFilter): Observable<ListAuditLogsOutcome> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);
    if (action) {
      params = params.set('action', action);
    }
    return this.http.get<PagedResult<AuditLogListItem>>(this.baseUrl, { params }).pipe(
      map((result): ListAuditLogsOutcome => ({ kind: 'page', result })),
      catchError((err: unknown) => {
        if (err instanceof HttpErrorResponse) {
          return of<ListAuditLogsOutcome>({ kind: 'error' });
        }
        return throwError(() => err);
      }),
    );
  }
}
