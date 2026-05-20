/**
 * Development environment configuration.
 *
 * Targets the backend running directly on the host at :5010 (DA-003).
 *
 * Note: the backend's CORS policy `AllowDocuPilotWeb` currently only
 * whitelists `http://localhost:4210`. Run the dev server with
 * `ng serve --port 4210` (or wait for the backend CORS policy to be
 * expanded) to avoid browser preflight failures.
 */
export const environment = {
  production: false,
  apiBaseUrl: 'http://localhost:5010',
};
