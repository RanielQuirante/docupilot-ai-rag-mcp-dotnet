import { Routes } from '@angular/router';

import { AskAiClient } from './data/ask-ai.client';

export const ASK_AI_ROUTES: Routes = [
  {
    path: '',
    // Slice-scoped data client — created when the slice is entered, torn down
    // when it is left (vertical-slice convention; DA-011 §3.3). NOT providedIn:'root'.
    providers: [AskAiClient],
    loadComponent: () => import('./pages/ask-ai-page/ask-ai-page').then((m) => m.AskAiPage),
  },
];
