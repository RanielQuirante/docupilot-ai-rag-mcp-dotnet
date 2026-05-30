import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpResponse } from '@angular/common/http';
import { Observable } from 'rxjs';

import { environment } from '@env/environment';

import { AuditLogEntry, DocumentDetail, DocumentTextResponse } from './document-detail.models';

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
}
