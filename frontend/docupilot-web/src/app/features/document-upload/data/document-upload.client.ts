import { Injectable, inject } from '@angular/core';
import {
  HttpClient,
  HttpEvent,
  HttpRequest,
} from '@angular/common/http';
import { Observable } from 'rxjs';

import { environment } from '@env/environment';

import { UploadDocumentResponse } from './document-upload.models';

/**
 * Slice-scoped data client for the Document Upload feature.
 *
 * Intentionally NOT `providedIn: 'root'` — registered in the route's
 * `providers: []` (see `document-upload.routes.ts`) so it is instantiated only
 * when the `/upload` slice is entered (DA-011 §3.3 vertical-slice convention).
 *
 * POSTs `multipart/form-data` to the FROZEN contract `POST /api/documents/upload`
 * using the repeatable field name `files`, with `reportProgress`/`observe:'events'`
 * so the page can drive a per-batch upload progress bar.
 */
@Injectable()
export class DocumentUploadClient {
  private readonly http = inject(HttpClient);
  private readonly uploadUrl = `${environment.apiBaseUrl}/documents/upload`;

  /**
   * Uploads one or more files in a single multipart request.
   *
   * Returns the raw `HttpEvent` stream so the caller can react to
   * `UploadProgress` events and the final `Response` (which carries the
   * `{ uploaded, failed }` body). The body is typed on the terminal response.
   */
  upload(files: readonly File[]): Observable<HttpEvent<UploadDocumentResponse>> {
    const formData = new FormData();
    for (const file of files) {
      // Repeatable field name MUST be `files` per the frozen contract.
      formData.append('files', file, file.name);
    }

    const request = new HttpRequest<FormData>('POST', this.uploadUrl, formData, {
      reportProgress: true,
    });

    return this.http.request<UploadDocumentResponse>(request);
  }
}
