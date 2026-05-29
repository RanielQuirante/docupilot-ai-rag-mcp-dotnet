import { Routes } from '@angular/router';

export const ASK_AI_ROUTES: Routes = [
  {
    path: '',
    loadComponent: () => import('./pages/ask-ai-page/ask-ai-page').then((m) => m.AskAiPage),
  },
];
