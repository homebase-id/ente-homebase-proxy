# Ente → Homebase proxy: status

## Live tests

Ente's own test credentials were made on 2026-10-30 (Todd approved "Ente proxy tests" on
frodo). They're in `proxy/.credentials.json` (gitignored, mode 600). Run everything with:

```sh
HOMEBASE_CREDENTIALS=$PWD/.credentials.json ~/.dotnet/dotnet test
```

The key endpoint test runs **only if Ente's drive has no key attributes file yet**, and deletes
the one it made. Once the web app's key setup creates your real one, it skips. If Homebase
refuses the credentials, the tests fail with "credentials revoked: redo consent": run
`node --disable-warning=MODULE_TYPELESS_PACKAGE_JSON scripts/consent.mjs` again and approve.

## What works

| What | Checked by |
|---|---|
| `PUT /users/attributes`: stores the key attributes as one zkDrive file (fixed uniqueId), 403 if it exists, never overwrites | Live test against frodo, passing |
| `GET /users/session-validity/v2`: `{hasSetKeys, keyAttributes}` from that file, as sent; `{hasSetKeys:false}` if none | Live test against frodo, passing |
| Odin refusing the token → 401 `{"error":"invalid token"}` | Live test against frodo, passing |
| Odin's encryption in C# (AES-CBC, key headers, envelopes) | Unit tests against the same vectors as the TypeScript and Node clients |
| X-Auth-Token parsing; malformed → 401 `invalid token`, missing → 401 `missing token` | Unit and HTTP tests |
| Every Ente route in the five groups is mapped (none 404s) and each TODO says why | Test that parses `server/cmd/museum/main.go` |
| `/health` 200 with no dependencies; X-Request-Id; CORS for http://localhost:3020 only | HTTP tests against the real server (Kestrel on a free port) |
| Logs hold no token, query string or body | HTTP test with a captured logger |
| Docker image builds and runs (`/health` 200, a TODO route 501) | `docker build` and `docker run` |

26 tests pass with `HOMEBASE_CREDENTIALS` set (2 of them live against frodo, through the
running proxy); without it, 24 pass and the 2 live ones skip.

## What's 501 and why

131 Ente routes are TODOs that return 501 `{"code":"NOT_IMPLEMENTED","message":<why>}`. Each says
which single Odin call it would be, or why it can't be one. The reasons, by count:

| Reason | Routes | What it means |
|---|---|---|
| Public links | 31 | Odin's anonymous access is a different model; no single call matches |
| int64 IDs | 22 (plus most others) | Ente's file and collection IDs are int64; Odin's are GUIDs. Needs a mapping (state) or an ID scheme agreed with the client. **This blocks almost every file and collection route.** |
| YouAuth | 20 | Ente's sign-in (OTT, SRP, 2FA, passkeys); the web app signs in with Homebase instead |
| Account | 13 | Billing, sessions, account deletion: the identity owns the account |
| Presigned URLs | 12 | Ente clients upload and download straight to S3; Odin has no presigned URLs |
| Other users | 12 | Sharing with Ente users by email or ID; Homebase shares between identities |
| Derived file data | 5 | ML and video previews, as payloads; blocked by the ID mapping |
| Diff | 4 | Ente's `sinceTime` vs Odin's `cursorState`, plus the ID mapping |

Others are one Odin call short or over: e.g. `PUT /users/recovery-key` and `/users/srp/update`
would PATCH the key attributes file, which needs its versionTag from a read first (two calls).

## Decisions for Todd

1. **IDs.** Nearly every file and collection route needs Ente's int64 IDs mapped to Odin's
   GUIDs. Options: a lookup table (state: breaks the rules), or a deterministic scheme (e.g.
   the uniqueId is derived from the int64, and the client keeps the int64 in the encrypted
   record). This is the next design decision.
2. **Uploads and downloads.** Ente clients use presigned S3 URLs. The proxy can't offer those;
   the client would have to send bytes through the proxy, which is a change to Ente's client.
3. **Two-call routes.** A few routes need a read before a write (versionTag). Allow two calls
   for those, or leave them 501?
4. **`POST /users/logout`** has nothing to do on Homebase. Answer 200 without an Odin call?

## Design

- **Stateless.** Each request's X-Auth-Token is base64url of
  `{"v":1,"identity","token","ss","dk"}`; the proxy decodes it, calls
  `https://<identity>/api/v2/...` with `Authorization: Bearer <token>`, and stores nothing.
  No caching, sessions, cookies, retries, queues, database or file writes.
- **One Odin call per handler**, with Odin's encryption as part of it: shared-secret envelopes,
  and zkDrive files (a fresh file key locked with the drive key). Ente's own encrypted data
  passes through untouched.
- **Ente's shapes**: 401 `{"error":"missing token"}` / `{"error":"invalid token"}` as museum's
  auth middleware; 403 `{}` for key attributes already set, as museum; 501 in Ente's ApiError
  shape. Homebase failures other than 401/403 are 502 `HOMEBASE_ERROR`.
- **Ente's drive**: alias and the key attributes file's uniqueId and fileType are in
  `EnteDrive.cs`, copied from `web/packages/homebase/constants.ts`. The password file is never
  touched.
- **Middleware**: X-Request-Id (kept if it's a plain token, else new), request logging
  (method, path, status, duration), CORS (`ALLOWED_ORIGINS`, default http://localhost:3020).
  ASP.NET's and HttpClient's own request logs are turned down to Warning, since they include
  URLs and query strings.

## How to run

```sh
cd proxy
~/.dotnet/dotnet run --project src/EnteHomebaseProxy     # http://localhost:8095
~/.dotnet/dotnet test                                    # offline tests; live ones skip
docker compose up --build                                # the same, in Docker
```

.NET 10 (SDK 10.0.402) is installed in `~/.dotnet`, not on the default PATH; `global.json`
pins it.
