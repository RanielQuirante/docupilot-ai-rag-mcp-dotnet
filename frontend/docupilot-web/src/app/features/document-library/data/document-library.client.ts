import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';

import { environment } from '@env/environment';

import { DocumentListItem, PagedResult } from './document.models';

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
}
