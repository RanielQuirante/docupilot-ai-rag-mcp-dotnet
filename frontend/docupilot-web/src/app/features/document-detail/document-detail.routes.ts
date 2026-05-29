import { Routes } from '@angular/router';

export const DOCUMENT_DETAIL_ROUTES: Routes = [
  { path: '', redirectTo: '/library', pathMatch: 'full' },
  {
    path: ':id',
    loadComponent: () =>
      import('./pages/document-detail-page/document-detail-page').then(
        (m) => m.DocumentDetailPage,
      ),
  },
];
