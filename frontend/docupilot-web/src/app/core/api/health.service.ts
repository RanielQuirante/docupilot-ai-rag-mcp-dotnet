import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

import { environment } from '../../../environments/environment';

/**
 * Shape of the JSON returned by the backend `GET /health` endpoint
 * (see `outputs/tech-lead-output.md` section 4 and DA-003).
 */
export interface HealthResponse {
  status: string;
  service: string;
  version: string;
  timestamp: string;
}

/**
 * Thin HTTP wrapper around the backend health endpoint.
 *
 * Intentionally minimal: no error mapping, no retry, no caching. Callers
 * (e.g. the root `App` component) decide how to render loading / success /
 * error states.
 */
@Injectable({ providedIn: 'root' })
export class HealthService {
  private readonly http = inject(HttpClient);

  getHealth(): Observable<HealthResponse> {
    return this.http.get<HealthResponse>(`${environment.apiBaseUrl}/health`);
  }
}
