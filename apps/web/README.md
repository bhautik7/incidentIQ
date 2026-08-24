# IncidentIQ web

React 19 + TypeScript (Vite), served in production by nginx.

Phase 2 renders a single page: the readiness of every backend service. It exists
to prove the stack is wired together, and will be replaced by the incident list
in Phase 3.

```bash
npm install
npm run dev        # :5173, against whatever is running in Docker
npm run build      # tsc -b && vite build
npm run typecheck
npm run lint
```

## Runtime configuration

A static bundle cannot read environment variables, so endpoints are injected at
container start: `docker-entrypoint.d/10-incidentiq-config.sh` regenerates
`/config.js` from `WEB_*` variables, and `src/config.ts` reads
`window.__INCIDENTIQ_CONFIG__`. One built image is therefore promotable across
environments.

`public/config.js` holds the local-development defaults used by `npm run dev`.

These URLs are **host** addresses (`http://localhost:5080`), not compose service
names - the bundle runs in the browser, which is not on the Docker network.
