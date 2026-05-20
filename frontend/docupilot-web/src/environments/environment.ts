/**
 * Production environment configuration.
 *
 * In production the Angular SPA is served via NGINX, which reverse-proxies
 * `/api/*` to the in-network `docupilot-api` container (see DA-006/007).
 * Calls made against `${apiBaseUrl}/health` therefore resolve to
 * `/api/health` and are forwarded to `http://docupilot-api:8080/health`
 * inside the Docker network — no cross-origin / CORS hop required.
 */
export const environment = {
  production: true,
  apiBaseUrl: '/api',
};
