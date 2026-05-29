import { Routes } from '@angular/router';

export const AUDIT_LOGS_ROUTES: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./pages/audit-logs-page/audit-logs-page').then((m) => m.AuditLogsPage),
  },
];
