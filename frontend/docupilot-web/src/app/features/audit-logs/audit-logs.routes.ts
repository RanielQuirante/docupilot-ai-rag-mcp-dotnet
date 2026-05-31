import { Routes } from '@angular/router';

import { AuditLogsClient } from './data/audit-logs.client';

export const AUDIT_LOGS_ROUTES: Routes = [
  {
    path: '',
    // Slice-scoped data client — created when the slice is entered, torn down
    // when it is left (vertical-slice convention; DA-011 §3.3). NOT providedIn:'root'.
    providers: [AuditLogsClient],
    loadComponent: () =>
      import('./pages/audit-logs-page/audit-logs-page').then((m) => m.AuditLogsPage),
  },
];
