import { Routes } from '@angular/router';

import { DocumentLibraryClient } from './data/document-library.client';

export const DOCUMENT_LIBRARY_ROUTES: Routes = [
  {
    path: '',
    // Slice-scoped data client — created when the slice is entered, torn down
    // when it is left (vertical-slice convention; DA-011 §3.3). NOT providedIn:'root'.
    providers: [DocumentLibraryClient],
    loadComponent: () =>
      import('./pages/document-library-page/document-library-page').then(
        (m) => m.DocumentLibraryPage,
      ),
  },
];
