import { Routes } from '@angular/router';

import { DashboardClient } from './data/dashboard.client';

export const DASHBOARD_ROUTES: Routes = [
  {
    path: '',
    // Slice-scoped data client — created when the slice is entered, torn down
    // when it is left (vertical-slice convention; DA-011 §3.3). NOT providedIn:'root'.
    providers: [DashboardClient],
    loadComponent: () =>
      import('./pages/dashboard-page/dashboard-page').then((m) => m.DashboardPage),
  },
];
