# The contract with the Ente web app

This proxy and the Ente web app ([homebase-id/homebase-ente](https://github.com/homebase-id/homebase-ente),
`web/packages/homebase`) are built separately but must agree on the following. Change one side,
change the other in the same piece of work, and keep the tests on both sides passing.

| What | Here | In the web app | Checked by |
|---|---|---|---|
| App registration (appId, slug `ente-photos`, name) and Ente's drive (alias, type, slugs, zero-knowledge) | `contract/ente-homebase.json`; `scripts/consent.mjs` reads it | `constants.ts` (`enteApp`, `enteDrive`) | `ContractTests.TheWebAppHasTheSameValues` (with an Ente checkout) |
| Fixed files on the drive: password file (web app only; the proxy never touches it), key attributes file | `contract/ente-homebase.json`; `EnteDrive.cs` | `constants.ts` (`passwordFile`, `keyAttributesFile`) | `ContractTests` |
| **X-Auth-Token**: base64url of `{"v":1,"identity","token","ss","dk"}` (clientAuthToken, shared secret, opened drive key; base64). A secret: headers only, never URLs or logs | `HomebaseCredentials.cs` | `ente.ts` `xAuthToken` | `CredentialsTests`; web `ente.test.ts` |
| **Ente user ID** of an identity: first 6 bytes of SHA-256(identity) | `EnteIds.UserId` | `ente.ts` `enteUserID` | Both test `frodo.baggins.demo.rocks` → `256750248973160` (in the contract file) |
| Email shown for an identity: `<identity>@homebase.invalid` | `EnteIds.Email` | `ente.ts` `enteEmail` | |
| Odin's encryption (AES-CBC, key headers, envelopes) | `OdinCrypto.cs` | `crypto.ts`, `context.ts`, `drive.ts` | Both test against `contract/vectors.json` (copied from the web app's `vectors.json`) |
| Homebase mode: credentials only in headers; uploads `PUT` to the URL the proxy gives, single-part; downloads via `/files/download/{id}` and `/files/preview/{id}` with the header | `FilesHandlers.cs` | `ente-base/origins` `isHomebaseMode`, `file-download.ts`, upload `remote.ts` | Browser check (STATUS.md, Milestones) |
| Ente's API itself: routes, status codes, JSON shapes | the handlers | Ente's server, `server/cmd/museum/main.go`, `server/ente/` | `ProxyBehaviorTests.EveryEnteRouteInTheGroupsIsMapped` (with an Ente checkout) |

The Ente checkout the tests compare against: `ENTE_REPO=/path/to/homebase-ente`, or an `ente`
folder next to this repo. Without one, those tests skip.
