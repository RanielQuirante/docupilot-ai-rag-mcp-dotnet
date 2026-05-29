import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

/**
 * Root host component.
 *
 * Thin shell host — just a `<router-outlet />`. The layout chrome (sidebar +
 * health widget) lives in `core/layout/shell`, loaded by the `path: ''` route.
 * The original DA-005 health-card logic moved to `core/layout/status/health-status`.
 */
@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {}
