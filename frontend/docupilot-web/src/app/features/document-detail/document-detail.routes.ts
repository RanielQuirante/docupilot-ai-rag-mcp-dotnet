import { Routes } from '@angular/router';

import { DocumentDetailClient } from './data/document-detail.client';

export const DOCUMENT_DETAIL_ROUTES: Routes = [
  { path: '', redirectTo: '/library', pathMatch: 'full' },
  {
    path: ':id',
    // Slice-scoped data client — created when the slice is entered, torn down
    // when it is left (vertical-slice convention; DA-011 §3.3). NOT providedIn:'root'.
    providers: [DocumentDetailClient],
    loadComponent: () =>
      import('./pages/document-detail-page/document-detail-page').then(
        (m) => m.DocumentDetailPage,
      ),
  },
];
