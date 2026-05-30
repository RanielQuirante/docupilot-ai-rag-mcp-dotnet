import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Observable, catchError, map, of, throwError } from 'rxjs';

import { environment } from '@env/environment';

import { SearchRequest, SearchResponse } from './search.models';

/**
 * Outcome of `POST /api/search`, mapped from the FROZEN Phase-6 contract
 * (backend DA-045):
 *  - `results` → 200 with the ranked (possibly empty) result set;
 *  - `'unavailable'` → 503 (embedder/Qdrant starting or down) — a clean,
 *    retryable signal the page surfaces distinctly from a generic error;
 *  - `'error'` → any other transport/server failure (incl. an unexpected 500).
 *
 * The 400 (empty query) is guarded client-side (the page disables the button /
 * no-ops on a blank query), so it is not modelled here as an expected outcome.
 */
export type SearchOutcome =
  | { readonly kind: 'results'; readonly response: SearchResponse }
  | { readonly kind: 'unavailable' }
  | { readonly kind: 'error' };

/**
 * Slice-scoped HTTP client for Semantic Search.
 *
 * Registered in the route's `providers: []` (NOT `providedIn:'root'`) per the
 * vertical-slice convention (Tech Lead ADR §3.3 / DA-011 §3.3): created when
 * the `/search` slice is entered and torn down with it.
 *
 * It maps the contract's status codes to a discriminated {@link SearchOutcome}
 * (mirroring `DocumentLibraryClient`'s `ProcessOutcome` pattern) so the page can
 * render results / empty / unavailable / error without bespoke error plumbing.
 */
@Injectable()
export class SemanticSearchClient {
  private readonly http = inject(HttpClient);
  private readonly searchUrl = `${environment.apiBaseUrl}/search`;

  /**
   * Run a natural-language search (`POST /api/search`).
   *
   * @param query    The NL search text (non-empty — the page guards blanks).
   * @param category Optional exact-match classification filter.
   */
  search(query: string, category?: string): Observable<SearchOutcome> {
    const body: SearchRequest = category ? { query, category } : { query };
    return this.http.post<SearchResponse>(this.searchUrl, body).pipe(
      map((response): SearchOutcome => ({ kind: 'results', response })),
      catchError((err: unknown) => {
        if (err instanceof HttpErrorResponse) {
          if (err.status === 503) {
            return of<SearchOutcome>({ kind: 'unavailable' });
          }
          return of<SearchOutcome>({ kind: 'error' });
        }
        return throwError(() => err);
      }),
    );
  }
}
