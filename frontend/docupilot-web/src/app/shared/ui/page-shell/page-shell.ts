import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * Reusable presentational wrapper for a feature page.
 *
 * Renders a consistent page header (title + optional subtitle) and projects
 * the page body via `<ng-content />`. Dumb component: no data access, driven
 * entirely by its `input()`s. Consumed by every feature placeholder page.
 */
@Component({
  selector: 'app-page-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './page-shell.html',
})
export class PageShell {
  /** Page title shown as the `<h1>` (required). */
  readonly title = input.required<string>();

  /** Optional secondary line under the title. */
  readonly subtitle = input<string>('');
}
