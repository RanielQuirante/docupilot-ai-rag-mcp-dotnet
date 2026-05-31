import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Observable, catchError, map, of, throwError } from 'rxjs';

import { environment } from '@env/environment';

import { AskRequest, AskResponse } from './ask.models';

/**
 * Outcome of `POST /api/ask`, mapped from the FROZEN Phase-7 contract
 * (backend DA-049):
 *  - `answer` → 200 with the answer + citations (covers BOTH a grounded answer
 *    and the not-found case — the page reads `response.answerFound` to split
 *    `answered` vs `notFound`);
 *  - `'unavailable'` → 503 (embedder/Qdrant/LLM starting or down) — a clean,
 *    retryable signal the page surfaces distinctly from a generic error;
 *  - `'error'` → any other transport/server failure (incl. an unexpected 500).
 *
 * The 400 (empty question) is guarded client-side (the page disables the Ask
 * button / no-ops on a blank question), so it is not modelled here as an
 * expected outcome. Mirrors the Phase-6 `SemanticSearchClient.SearchOutcome`.
 */
export type AskOutcome =
  | { readonly kind: 'answer'; readonly response: AskResponse }
  | { readonly kind: 'unavailable' }
  | { readonly kind: 'error' };

/**
 * Slice-scoped HTTP client for Ask AI (RAG Q&A).
 *
 * Registered in the route's `providers: []` (NOT `providedIn:'root'`) per the
 * vertical-slice convention (Tech Lead ADR §3.3 / DA-011 §3.3): created when
 * the `/ask` slice is entered and torn down with it.
 *
 * It maps the contract's status codes to a discriminated {@link AskOutcome}
 * (mirroring `SemanticSearchClient`'s `SearchOutcome`) so the page can render
 * answered / not-found / unavailable / error without bespoke error plumbing.
 */
@Injectable()
export class AskAiClient {
  private readonly http = inject(HttpClient);
  private readonly askUrl = `${environment.apiBaseUrl}/ask`;

  /**
   * Ask a natural-language question over the uploaded documents
   * (`POST /api/ask`). The chat LLM runs on CPU and can take tens of seconds —
   * the caller shows a "Thinking…" affordance; HttpClient must not abort early.
   *
   * @param question The NL question (non-empty — the page guards blanks).
   * @param topK     Optional number of chunks to retrieve as context.
   * @param category Optional exact-match classification filter (scope the ask).
   */
  ask(question: string, topK?: number, category?: string): Observable<AskOutcome> {
    const body: AskRequest = { question };
    if (topK !== undefined) {
      (body as { topK?: number }).topK = topK;
    }
    if (category) {
      (body as { category?: string }).category = category;
    }
    return this.http.post<AskResponse>(this.askUrl, body).pipe(
      map((response): AskOutcome => ({ kind: 'answer', response })),
      catchError((err: unknown) => {
        if (err instanceof HttpErrorResponse) {
          if (err.status === 503) {
            return of<AskOutcome>({ kind: 'unavailable' });
          }
          return of<AskOutcome>({ kind: 'error' });
        }
        return throwError(() => err);
      }),
    );
  }
}
