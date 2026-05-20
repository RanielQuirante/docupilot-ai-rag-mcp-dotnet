import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterOutlet } from '@angular/router';

import { environment } from '../environments/environment';
import { HealthResponse, HealthService } from './core/api/health.service';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App implements OnInit {
  private readonly health = inject(HealthService);
  private readonly destroyRef = inject(DestroyRef);

  /** Loading flag — true from init until the first response/error arrives. */
  protected readonly loading = signal<boolean>(true);

  /** Successful health payload, or `null` while loading / on error. */
  protected readonly data = signal<HealthResponse | null>(null);

  /** Human-readable error message, or `null` when there is no error. */
  protected readonly error = signal<string | null>(null);

  /** Exposed to the template so the error banner can name the URL we tried. */
  protected readonly apiBaseUrl = environment.apiBaseUrl;

  ngOnInit(): void {
    this.health
      .getHealth()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (response) => {
          this.data.set(response);
          this.error.set(null);
          this.loading.set(false);
        },
        error: (err: unknown) => {
          // We intentionally do not surface raw error objects in the UI —
          // the template renders a friendly message that includes the base
          // URL and a CORS-port hint (Phase 1 limitation). The console keeps
          // the original error for debugging.
          console.error('GET /health failed', err);
          this.data.set(null);
          this.error.set('Unable to reach the DocuPilot API.');
          this.loading.set(false);
        },
      });
  }
}
