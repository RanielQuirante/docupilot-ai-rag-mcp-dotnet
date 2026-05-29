import { Routes } from '@angular/router';

export const DOCUMENT_UPLOAD_ROUTES: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./pages/document-upload-page/document-upload-page').then(
        (m) => m.DocumentUploadPage,
      ),
  },
];
