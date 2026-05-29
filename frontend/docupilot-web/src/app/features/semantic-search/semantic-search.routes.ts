import { Routes } from '@angular/router';

export const SEMANTIC_SEARCH_ROUTES: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./pages/semantic-search-page/semantic-search-page').then(
        (m) => m.SemanticSearchPage,
      ),
  },
];
