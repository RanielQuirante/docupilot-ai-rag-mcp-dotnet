/**
 * A single primary-navigation entry rendered in the sidebar.
 *
 * `path` is an absolute router path (e.g. `/dashboard`) bound to `routerLink`;
 * `label` is the visible text. The canonical list lives in
 * `core/layout/nav/nav-items.ts` and must stay in sync with the lazy slice
 * paths declared in `app.routes.ts`.
 */
export interface NavItem {
  label: string;
  path: string;
}
