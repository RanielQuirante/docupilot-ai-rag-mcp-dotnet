import { NavItem } from '@core/models/nav-item.model';

/**
 * Single source of truth for the sidebar primary navigation.
 *
 * Paths must stay in sync with the lazy slice paths in `app.routes.ts`.
 * `document-detail` (`/documents/:id`) is intentionally NOT listed here — it
 * is reached from the Document Library, not the top-level nav.
 */
export const NAV_ITEMS: readonly NavItem[] = [
  { label: 'Dashboard', path: '/dashboard' },
  { label: 'Document Upload', path: '/upload' },
  { label: 'Document Library', path: '/library' },
  { label: 'Semantic Search', path: '/search' },
  { label: 'Ask AI', path: '/ask' },
  { label: 'Workflow Tasks', path: '/tasks' },
  { label: 'Audit Logs', path: '/audit' },
];
