import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { HealthResponse, HealthService } from '@core/api/health.service';

/**
 * Compact API health indicator rendered in the shell sidebar footer.
 *
 * Owns the `GET /health` call (logic moved here from the original `App`
 * component in DA-013) and exposes loading / healthy / unreachable state as
 * signals. Reuses the unchanged `core/api/health.service.ts`.
 */
@Component({
  selector: 'app-health-status',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './health-status.html',
})
export class HealthStatus implements OnInit {
  private readonly health = inject(HealthService);
  private readonly destroyRef = inject(DestroyRef);

  /** True from init until the first response/error arrives. */
  protected readonly loading = signal<boolean>(true);

  /** Successful health payload, or `null` while loading / on error. */
  protected readonly data = signal<HealthResponse | null>(null);

  /** True once a request has failed (API unreachable). */
  protected readonly failed = signal<boolean>(false);

  ngOnInit(): void {
    this.health
      .getHealth()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (response) => {
          this.data.set(response);
          this.failed.set(false);
          this.loading.set(false);
        },
        error: (err: unknown) => {
          // The widget shows a friendly "unreachable" badge; the original
          // error stays in the console for debugging. Phase 1 CORS only
          // allows http://localhost:4210, so dev must `ng serve --port 4210`.
          console.error('GET /health failed', err);
          this.data.set(null);
          this.failed.set(true);
          this.loading.set(false);
        },
      });
  }
}
