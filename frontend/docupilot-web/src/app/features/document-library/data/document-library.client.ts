import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Observable, catchError, map, of, throwError } from 'rxjs';

import { environment } from '@env/environment';

import { DocumentListItem, PagedResult } from './document.models';

/**
 * Outcome of `POST /api/documents/{id}/process`, mapped from the FROZEN
 * Phase-3 contract (DA-024): 202 → re-process accepted; 409 → already
 * Queued/ExtractingText (a no-op the UI can surface briefly); 404 → the row
 * disappeared server-side. Any other transport failure surfaces as `error`.
 */
export type ProcessOutcome = 'accepted' | 'conflict' | 'notFound' | 'error';

/**
 * Slice-scoped HTTP client for the Document Library.
 *
 * Registered in the route's `providers: []` (NOT `providedIn:'root'`) per the
 * vertical-slice convention (Tech Lead ADR §3.3 / DA-011 §3.3): the client is
 * created only when the `/library` slice is entered and torn down with it.
 *
 * Thin wrapper — no error mapping, retry, or caching. The page component owns
 * loading / success / empty / error presentation.
 */
@Injectable()
export class DocumentLibraryClient {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${environment.apiBaseUrl}/documents`;

  /**
   * Fetch one page of documents, newest-first.
   *
   * @param page     1-based page index.
   * @param pageSize Items per page (server caps at 100).
   */
  list(page: number, pageSize: number): Observable<PagedResult<DocumentListItem>> {
    const params = new HttpParams().set('page', page).set('pageSize', pageSize);
    return this.http.get<PagedResult<DocumentListItem>>(this.baseUrl, { params });
  }

  /**
   * Re-queue a document for processing (`POST /api/documents/{id}/process`).
   *
   * Returns a discriminated {@link ProcessOutcome} rather than throwing, so the
   * page can reflect the 202 / 404 / 409 contract states without bespoke error
   * plumbing. The expected non-2xx statuses (404/409) resolve to a value; any
   * other failure resolves to `'error'`.
   */
  process(id: string): Observable<ProcessOutcome> {
    return this.http.post(`${this.baseUrl}/${id}/process`, null, { observe: 'response' }).pipe(
      map((): ProcessOutcome => 'accepted'),
      catchError((err: unknown) => {
        if (err instanceof HttpErrorResponse) {
          if (err.status === 409) {
            return of<ProcessOutcome>('conflict');
          }
          if (err.status === 404) {
            return of<ProcessOutcome>('notFound');
          }
          return of<ProcessOutcome>('error');
        }
        return throwError(() => err);
      }),
    );
  }
}
