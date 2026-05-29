import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';

import { PageShell } from '@shared/ui/page-shell/page-shell';

/**
 * 404 page rendered INSIDE the shell (so the sidebar chrome is preserved).
 * Matched by the `'**'` wildcard child route in `app.routes.ts`.
 */
@Component({
  selector: 'app-not-found',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PageShell, RouterLink],
  template: `
    <app-page-shell title="Page not found" subtitle="Error 404">
      <p class="text-sm leading-relaxed text-slate-600">
        The page you are looking for does not exist or has moved.
      </p>
      <a
        routerLink="/dashboard"
        class="mt-4 inline-block rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white transition-colors hover:bg-indigo-500"
      >
        Back to Dashboard
      </a>
    </app-page-shell>
  `,
})
export class NotFound {}
