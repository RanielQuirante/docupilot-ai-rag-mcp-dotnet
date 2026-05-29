import { Routes } from '@angular/router';

export const DOCUMENT_LIBRARY_ROUTES: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./pages/document-library-page/document-library-page').then(
        (m) => m.DocumentLibraryPage,
      ),
  },
];
