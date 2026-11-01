# Standalone frontend

The current EE workspace lives in `preview/` and starts with `npm start` from the repository root. The Windows installer packages that workspace.

The older standalone frontend is retained at the root `index.html`, `src/app.mjs`, and `assets/style.css` for existing integrations. It shares the effect, color, and state modules under `src/`.

To generate its static files:

[WINDOWS POWERSHELL]

```powershell
npm run build:legacy
```

The build writes `dist/` and a SHA-256 asset manifest. Serve that generated directory with the backend's configured static-file path when using the standalone interface. This `web/` directory holds documentation rather than a second editable copy of the browser source.

Use [docs/DEVELOPMENT.md](../docs/DEVELOPMENT.md) for the current workspace and installer commands.
