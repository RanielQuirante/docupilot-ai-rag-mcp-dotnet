import { Routes } from '@angular/router';

import { SemanticSearchClient } from './data/semantic-search.client';

export const SEMANTIC_SEARCH_ROUTES: Routes = [
  {
    path: '',
    // Slice-scoped data client — created when the slice is entered, torn down
    // when it is left (vertical-slice convention; DA-011 §3.3). NOT providedIn:'root'.
    providers: [SemanticSearchClient],
    loadComponent: () =>
      import('./pages/semantic-search-page/semantic-search-page').then(
        (m) => m.SemanticSearchPage,
      ),
  },
];
