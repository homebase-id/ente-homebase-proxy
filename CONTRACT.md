# The contract with the Ente web app

This proxy and the Ente web app ([homebase-id/homebase-ente](https://github.com/homebase-id/homebase-ente),
`web/packages/homebase`) are built separately but must agree on the following. Change one side,
change the other in the same piece of work, and keep the tests on both sides passing.

| What | Here | In the web app | Checked by |
|---|---|---|---|
| App registration (appId, slug `ente-photos`, name) and Ente's drive (alias, type, slugs, zero-knowledge) | `contract/ente-homebase.json`; `scripts/consent.mjs` reads it | `constants.ts` (`enteApp`, `enteDrive`) | `ContractTests.TheWebAppHasTheSameValues` (with an Ente checkout) |
| Fixed files on the drive: password file (web app only; the proxy never touches it), key attributes file | `contract/ente-homebase.json`; `EnteDrive.cs` | `constants.ts` (`passwordFile`, `keyAttributesFile`) | `ContractTests` |
| **X-Auth-Token**: base64url of `{"v":2,"identity","token","ss"}` (clientAuthToken, shared secret; base64). **No drive key**: version 1 carried one and is refused. A secret: headers only, never URLs or logs | `HomebaseCredentials.cs` | `ente.ts` `xAuthToken` | `CredentialsTests`; web `ente.test.ts` |
| **Ente user ID** of an identity: first 6 bytes of SHA-256(identity) | `EnteIds.UserId` | `ente.ts` `enteUserID` | Both test `frodo.baggins.demo.rocks` → `256750248973160` (in the contract file) |
| Email shown for an identity: `<identity>@homebase.invalid` | `EnteIds.Email` | `ente.ts` `enteEmail` | |
| Odin's transport encryption (shared-secret envelopes) | `OdinCrypto.cs` | `crypto.ts`, `context.ts` | Both test against `contract/vectors.json` (copied from the web app's `vectors.json`) |
| **A sealed record** (chat-kmp's model): `{"keyHeader", "content"}`. The web app gives each file a random AES-128 key and IV, locks it with the drive key (a `zkDrive` key header, 48 bytes), and encrypts the record's JSON with it as `appData.content`. The proxy checks the shape, stores both as sent, and returns them as `"homebase"`; it refuses any other key header type | `Odin/SealedRecord.cs` | `records.ts`; `ente-media` `homebase.ts` | `CryptoVectorTests` (the `keyHeader` vector); web `records.test.ts`; `LivePhotoTests` |
| What's sealed: key attributes (Ente's `KeyAttributes`); an album (`{type, name, magicMetadata?, pubMagicMetadata?}`); a photo (`{metadata, magicMetadata?, pubMagicMetadata?}`), all plain JSON with no Ente encryption. An album membership is unencrypted and empty | `EnteDrive.cs`, the handlers | `apps/photos` `homebase.ts`; `ente-media` `collection.ts` `HomebaseCollectionDetails`, `file.ts` `HomebaseFileDetails` | `LiveKeyEndpointTests`, `LivePhotoTests` |
| Homebase mode: credentials only in headers; a photo uploads in one multipart `POST /homebase/files` (parts `request` with `fileID`, `collectionID`, `homebase`, `payloadIv`, `contentType`, `thumbnails`, `previewThumbnail`; one `thumbnail` per listed thumbnail; `file`, in that order). The original and thumbnails are encrypted with the photo's key and `payloadIv`; reads via `/homebase/files/{id}/thumb` and `/original`, as stored | `FilesHandlers.cs` | `ente-base/origins` `isHomebaseMode`, `file-download.ts` `fetchHomebasePhoto`, upload `remote.ts` `postHomebaseEnteFile`, `upload-service.ts` `uploadToHomebase`, `download-core.ts` `downloadHomebasePhoto` | Browser check (STATUS.md, Milestones) |
| A photo: fileType 7210, payload key `original`; Ente's record says `metadata.decryptionHeader` `homebase-v2`, and `file`/`thumbnail` `decryptionHeader` `homebase-v2:<payload IV, base64>` | `contract/ente-homebase.json` `photo`; `EnteDrive.cs`, `EnteRecords.File` | `constants.ts` `photo`, `photo.ts` | `ContractTests.TheWebAppHasTheSameValues`; `photo.test.ts`; `LivePhotoTests` |
| Ente's API itself: routes, status codes, JSON shapes | the handlers | Ente's server, `server/cmd/museum/main.go`, `server/ente/` | `ProxyBehaviorTests.EveryEnteRouteInTheGroupsIsMapped` (with an Ente checkout) |

The Ente checkout the tests compare against: `ENTE_REPO=/path/to/homebase-ente`, or an `ente`
folder next to this repo. Without one, those tests skip.
