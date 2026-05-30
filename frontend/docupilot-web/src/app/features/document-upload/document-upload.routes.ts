import { Routes } from '@angular/router';

import { DocumentUploadClient } from './data/document-upload.client';

export const DOCUMENT_UPLOAD_ROUTES: Routes = [
  {
    path: '',
    // Slice-scoped data client — created only when the /upload slice is
    // entered (DA-011 §3.3), NOT providedIn:'root'.
    providers: [DocumentUploadClient],
    loadComponent: () =>
      import('./pages/document-upload-page/document-upload-page').then(
        (m) => m.DocumentUploadPage,
      ),
  },
];
