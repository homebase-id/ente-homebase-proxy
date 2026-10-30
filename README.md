# ente-homebase-proxy

A stateless HTTP proxy that stands in for Ente's server for Ente Photos users who sign in with
Homebase: Ente's REST API in, the user's own Odin Core identity out. C#, .NET 10.

- What works, what doesn't, and the design decisions: [STATUS.md](STATUS.md)
- What it shares with the Ente web app ([homebase-id/homebase-ente](https://github.com/homebase-id/homebase-ente)): [CONTRACT.md](CONTRACT.md)

```sh
~/.dotnet/dotnet run --project src/EnteHomebaseProxy   # http://localhost:8095
~/.dotnet/dotnet test                                  # live tests need HOMEBASE_CREDENTIALS
docker compose up --build
```
