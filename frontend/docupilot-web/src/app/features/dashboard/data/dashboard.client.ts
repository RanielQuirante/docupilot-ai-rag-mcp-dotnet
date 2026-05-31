import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Observable, catchError, map, of, throwError } from 'rxjs';

import { environment } from '@env/environment';

import { DashboardStats } from './dashboard.models';

/**
 * Outcome of `GET /api/dashboard/stats`, mapped from the FROZEN Phase-9
 * contract (backend DA-058):
 *  - `stats` → 200 with the aggregate metrics (empty DB ⇒ all zeros);
 *  - `'error'` → any transport/server failure (the contract has no other
 *    success/failure codes, so anything non-200 is a generic error).
 *
 * Mirrors the discriminated-outcome pattern of `WorkflowTasksClient` /
 * `SemanticSearchClient` so the page renders its states without bespoke error
 * plumbing.
 */
export type DashboardStatsOutcome =
  | { readonly kind: 'stats'; readonly stats: DashboardStats }
  | { readonly kind: 'error' };

/**
 * Slice-scoped HTTP client for the Dashboard landing page.
 *
 * Registered in the route's `providers: []` (NOT `providedIn:'root'`) per the
 * vertical-slice convention (Tech Lead ADR §3.3 / DA-011 §3.3): created when
 * the `/dashboard` slice is entered and torn down with it.
 */
@Injectable()
export class DashboardClient {
  private readonly http = inject(HttpClient);
  private readonly url = `${environment.apiBaseUrl}/dashboard/stats`;

  /** `GET /api/dashboard/stats` → `200 DashboardStats`. */
  getStats(): Observable<DashboardStatsOutcome> {
    return this.http.get<DashboardStats>(this.url).pipe(
      map((stats): DashboardStatsOutcome => ({ kind: 'stats', stats })),
      catchError((err: unknown) => {
        if (err instanceof HttpErrorResponse) {
          return of<DashboardStatsOutcome>({ kind: 'error' });
        }
        return throwError(() => err);
      }),
    );
  }
}
