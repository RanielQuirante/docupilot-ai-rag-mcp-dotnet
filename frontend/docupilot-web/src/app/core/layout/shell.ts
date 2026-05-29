import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

import { SidebarNav } from '@core/layout/nav/sidebar-nav';

/**
 * Application layout shell.
 *
 * Loaded by the `path: ''` route in `app.routes.ts`; renders the persistent
 * sidebar chrome plus a `<router-outlet />` for the active feature slice (and
 * the in-shell 404). Every routed page lives inside this shell.
 */
@Component({
  selector: 'app-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, SidebarNav],
  templateUrl: './shell.html',
  styleUrl: './shell.css',
})
export class Shell {}
