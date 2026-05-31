import { Routes } from '@angular/router';

import { WorkflowTasksClient } from './data/workflow-tasks.client';

export const WORKFLOW_TASKS_ROUTES: Routes = [
  {
    path: '',
    // Slice-scoped data client — created when the slice is entered, torn down
    // when it is left (vertical-slice convention; DA-011 §3.3). NOT providedIn:'root'.
    providers: [WorkflowTasksClient],
    loadComponent: () =>
      import('./pages/workflow-tasks-page/workflow-tasks-page').then((m) => m.WorkflowTasksPage),
  },
];
