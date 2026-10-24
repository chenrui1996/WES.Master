# T03/T04 operations client test record

- Status: verified software client slice; T03/T04 overall remain incomplete.
- Scope: Ops Web component tests, strict type/build check, and controlled browser acceptance.

## Results

| Check | Command / fixture | Result |
|---|---|---|
| Component suite | `npm test` in `clients\ops-web` | 12 files, 73 passed, 0 failed |
| Strict type-check and production build | `npm run build` in `clients\ops-web` | `vue-tsc --noEmit` and Vite build passed |
| Real browser | `RealBrowserUsesHttpsOwnerIdentityAndAllNamedPlatformPages` through the controlled .NET fixture | 1 passed; HTTPS Kestrel, built same-origin client, identity, named platform pages, authorized writes, and logout |

The browser fixture obtains its URL from the test host, uses a generated certificate and `wes_test`, and disables trace/video/screenshot recording. It refuses to run without the fixture-provided `WES_E2E_URL`; do not point Playwright at a production endpoint.

## Boundary

The browser result is not a production deployment, physical PDA/device compatibility, real breach-corpus certification, or human review at 200% browser zoom. UI visibility and client-side preferences do not replace server authorization. See [the implementation record](20-t03-t04-operations-client.md) and the [deployment/recovery manual](21-t03-t04-deployment.md) for remaining obligations.
