import { Routes } from '@angular/router';

export const WORKFLOW_TASKS_ROUTES: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./pages/workflow-tasks-page/workflow-tasks-page').then((m) => m.WorkflowTasksPage),
  },
];
