import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';

import { HealthStatus } from '@core/layout/status/health-status';
import { NAV_ITEMS } from '@core/layout/nav/nav-items';

/**
 * Sidebar primary navigation.
 *
 * Renders `NAV_ITEMS` as `routerLink`s with `routerLinkActive` highlighting,
 * and hosts the `<app-health-status />` widget at the bottom. Dumb component:
 * the nav list is a static module-level constant, not fetched data.
 */
@Component({
  selector: 'app-sidebar-nav',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, RouterLinkActive, HealthStatus],
  templateUrl: './sidebar-nav.html',
})
export class SidebarNav {
  protected readonly items = NAV_ITEMS;
}
