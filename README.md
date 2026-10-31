# ente-homebase-proxy

A stateless HTTP proxy that stands in for Ente's server for Ente Photos users who sign in with
Homebase: Ente's REST API in, the user's own Odin Core identity out. C#, .NET 10.

- What works, what doesn't, and the design decisions: [STATUS.md](STATUS.md)
- What it shares with the Ente web app ([homebase-id/homebase-ente](https://github.com/homebase-id/homebase-ente)): [CONTRACT.md](CONTRACT.md)

```sh
~/.dotnet/dotnet run --project src/EnteHomebaseProxy   # http://localhost:8095
~/.dotnet/dotnet test                                  # live tests need HOMEBASE_CREDENTIALS
```

## The web app, on the same site

The proxy also serves the Ente Photos web app, built from homebase-ente at the commit in
[ente.ref](ente.ref): pages at `/`, the API under `/api` (it also still answers at `/`, which is
what the web app's dev server on port 3020 uses).

```sh
scripts/build-web.sh                 # builds ../ente (or ENTE_REPO) into client/photos
WEB_ROOT=$PWD/client/photos ~/.dotnet/dotnet run --project src/EnteHomebaseProxy
docker compose up --build            # the image includes client/photos
```

To move to a newer web app, push it to homebase-ente and put its full commit hash in `ente.ref`.

## Deploy

https://ente.ravenhosting.cloud, on blackbird-aux, the same way as raven-hosting:

1. Run **Build and deploy to production** (Actions, on any branch). It builds the web app at
   `ente.ref`, tests, and pushes `ghcr.io/homebase-id/ente-homebase-proxy-internal:<branch>`.
2. It then asks the Ansible controller to deploy that tag (`CONTROLLER_DEPLOY_KEY`, in the `deploy`
   environment). The controller only accepts what the DevOps repo allowlists; its playbook
   `deploy-ente-homebase-proxy.yml` runs the container on `127.0.0.1:8085`, behind Caddy.

Set up once: the `deploy` environment with `CONTROLLER_DEPLOY_KEY`, and the ghcr package made public, since
blackbird-aux pulls it without logging in.

Settings: `TRUST_FORWARDED_HEADERS=true` behind Caddy (so the proxy sees https), `WEB_ROOT`
(default `client/photos`), `ALLOWED_ORIGINS` (CORS, only for a web app on another origin).
