import { Routes } from '@angular/router';

/**
 * Top-level routing.
 *
 * A single shell route at `path: ''` loads the layout chrome
 * (`core/layout/shell`); every feature slice is a lazy `loadChildren` child so
 * the shell (sidebar + health widget) wraps every page, including the in-shell
 * 404. Each slice is its own lazy chunk (8 chunks total).
 */
export const routes: Routes = [
  {
    path: '',
    loadComponent: () => import('./core/layout/shell').then((m) => m.Shell),
    children: [
      { path: '', redirectTo: 'dashboard', pathMatch: 'full' },
      {
        path: 'dashboard',
        loadChildren: () =>
          import('./features/dashboard/dashboard.routes').then((m) => m.DASHBOARD_ROUTES),
      },
      {
        path: 'upload',
        loadChildren: () =>
          import('./features/document-upload/document-upload.routes').then(
            (m) => m.DOCUMENT_UPLOAD_ROUTES,
          ),
      },
      {
        path: 'library',
        loadChildren: () =>
          import('./features/document-library/document-library.routes').then(
            (m) => m.DOCUMENT_LIBRARY_ROUTES,
          ),
      },
      {
        path: 'documents',
        loadChildren: () =>
          import('./features/document-detail/document-detail.routes').then(
            (m) => m.DOCUMENT_DETAIL_ROUTES,
          ),
      },
      {
        path: 'search',
        loadChildren: () =>
          import('./features/semantic-search/semantic-search.routes').then(
            (m) => m.SEMANTIC_SEARCH_ROUTES,
          ),
      },
      {
        path: 'ask',
        loadChildren: () => import('./features/ask-ai/ask-ai.routes').then((m) => m.ASK_AI_ROUTES),
      },
      {
        path: 'tasks',
        loadChildren: () =>
          import('./features/workflow-tasks/workflow-tasks.routes').then(
            (m) => m.WORKFLOW_TASKS_ROUTES,
          ),
      },
      {
        path: 'audit',
        loadChildren: () =>
          import('./features/audit-logs/audit-logs.routes').then((m) => m.AUDIT_LOGS_ROUTES),
      },
      {
        path: '**',
        loadComponent: () => import('./core/layout/not-found/not-found').then((m) => m.NotFound),
      },
    ],
  },
];
