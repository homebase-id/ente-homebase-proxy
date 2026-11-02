# Ente → Homebase proxy: status

Moved out of the Ente fork (homebase-id/homebase-ente, `proxy/` on branch `proxy-v1`) into
this repo on 2026-10-30, with its history. What it shares with the web app is in CONTRACT.md.

## Live tests

Ente's own test credentials were made on 2026-10-30 (Todd approved "Ente proxy tests" on
frodo). They're in `.credentials.json` (gitignored, mode 600). Run everything with:

```sh
HOMEBASE_CREDENTIALS=$PWD/.credentials.json ~/.dotnet/dotnet test
```

The key endpoint test runs **only if Ente's drive has no key attributes file yet**, and deletes
the one it made. Once the web app's key setup creates your real one, it skips. If Homebase
refuses the credentials, the tests fail with "credentials revoked: redo consent": run
`node scripts/consent.mjs` again and approve.

## Milestones

**A. Sign in → empty gallery: works.** **B. Upload a photo and see it: works.** Both checked
on 2026-10-30 in headless Chrome against frodo.baggins.demo.rocks, through this proxy and the
web app (dev server on 3020):

1. Sign in with Homebase → `/homebase` → **Continue to Ente Photos**. The web app points
   Ente's API at the proxy, and Ente's keys are set up (first time) or unlocked with the
   hidden password. Both paths ran: the first run created the real key attributes file.
2. The gallery opens: user details, albums, album sync, trash, people/places key all answer;
   no console errors.
3. Upload a photo into Uncategorized (Ente creates that album through the proxy first): the
   photo and thumbnail objects are PUT, then the record is created.
4. **A fresh browser** (nothing cached) signs in and shows the photo: the album sync returns
   it, the thumbnail comes from Homebase, and opening it downloads and decrypts the original.

The test photo (a 320×240 checkerboard) is still in your Ente library on frodo.

## What works

| Route | Odin calls | Notes |
|---|---|---|
| `PUT /users/attributes`, `GET /users/session-validity/v2` | 1 | Key attributes file; 403 if set, never overwritten |
| `GET /users/details/v2` | 0 | The identity as an Ente user; a 1 TiB "homebase" plan. TODO: real usage |
| `POST /users/logout` | 0 | Nothing to do on Homebase |
| `POST /collections`, `GET /collections/{id}` | 1 | An album: Ente's collection (client-encrypted) as content |
| `GET /collections/v2` | 1 | Every album; each album's updationTime is *now* (see below) |
| `GET /collections/v2/diff` | 2 | The album's memberships, then their photo records |
| `POST /homebase/files` | 2 | A photo in one request (decision 2): one Homebase file, then its membership; up to 1 GiB, streamed; bytes stored as sent |
| `GET /homebase/files/{id}/thumb?width&height` | 1 | The thumbnail covering that size, as stored (the web app decrypts it) |
| `GET /homebase/files/{id}/original` | 1 | The original, as stored, streamed (the web app decrypts it) |
| `GET /files/preview/{id}`, `GET /files/download/{id}` | 1–3 | Only for photos from before the web app encrypted in Homebase's format (below) |
| `GET`/`POST /user-entity/key` | 1 | The key for one type of people/places data |
| `/remote-store/feature-flags`, `/contacts/diff`, `/trash/v2/diff`, `/user-entity/entity/diff`, `/collection-actions/pending-remove` | 0 | Empty: nothing of that kind can exist yet |

Plus: Odin's encryption checked against the TypeScript and Node vectors; X-Auth-Token parsing;
every Ente route in the five groups mapped and none mapped twice; `/health`; X-Request-Id; CORS
(`Content-MD5` allowed for uploads); logs hold no token, query or body; Docker image.

34 tests: 33 pass with `HOMEBASE_CREDENTIALS` set; the key endpoint test now skips, because
the real key attributes file exists.

## Worth knowing

- **Album updationTime is always now.** Ente bumps an album when its photos change, and the
  client only syncs an album whose time moved. Bumping on upload would be a third call, so
  instead every pull syncs every album (each sync returns only changes). The sync looks 10
  minutes back from sinceTime, since the client may store the proxy's clock while changes carry
  Odin's; repeats are harmless.
- **One layer of encryption on the media: Homebase's, applied in the browser.** The proxy
  never sees a photo in the clear: it stores and returns the bytes as the web app sent them.
  Ente's encryption stays only on its small record of each photo (dates, captions, the key that
  opens them), which Ente's sync needs. Other Homebase apps that can read the drive open the
  original and thumbnails like any other Homebase file.
- **Photo keys are derived** (`OdinCrypto.DeriveObjectKey`, and `photo.ts` `photoKey` in the
  web app: HKDF of the drive key and the photo's uniqueId). The web app needs no header to
  read a photo, and picks the photo's ID so it can derive the key before uploading. The key is
  also locked in the file's key header, so other Homebase apps open the file normally.
  `contract/ente-homebase.json` has a test value both sides check.
- **The inline preview isn't encrypted.** Homebase keeps every file's tiny `previewThumbnail`
  (20 px, under 1 KB) in its metadata as is, for every app (odin-js `encryptMetaData`
  encrypts only the content); Ente's photos now have one too.
- **Big videos are encrypted in browser memory.** WebCrypto's AES-CBC doesn't stream, so the
  web app holds the whole file and its ciphertext at once (up to the 1 GiB limit).
- **Photos from before the web app encrypted in Homebase's format** still read, through the
  Ente routes, which open Homebase's layer to hand Ente its own ciphertext:
  - before 2026-10-30's changes: the original and thumbnail as two object files (7204);
  - on 2026-10-30: one file with Ente's ciphertext as the `entefile` payload.
  Remove those paths once such photos are re-uploaded or deleted.
- **No paging yet**: an album sync reads at most 1000 memberships, the album list 1000 albums.
- **Security (prototype)**: the web app keeps the X-Auth-Token, which holds the drive key, where
  Ente keeps its auth token (localStorage and IndexedDB, unencrypted).
- **A failed upload leaves at most an orphaned photo with no album** (the photo file was made,
  its membership wasn't). The client doesn't retry, since a retry would add a second photo.

## What's 501 and why

The other Ente routes are TODOs that return 501 `{"code":"NOT_IMPLEMENTED","message":<why>}`;
each says which Odin calls it would be, or why not. With decisions 1–5 most file and album
TODOs are now buildable (add to album, rename, trash, metadata edits). Remaining reasons, as
first written (before the ID decision unblocked most of the "int64 IDs" ones):

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

## Decisions (2026-10-30, with Todd)

Built for milestones A and B (the routes above); the rest of the TODOs follow them.

1. **Ente IDs ↔ Odin uniqueIds, no state.** Every Ente ID maps to a GUID: one type byte,
   zeros, then the number. Membership records also carry the album ID:

   | Type byte | What | GUID |
   |---|---|---|
   | `01` | photo (file record) | `01` ‖ 7 zero bytes ‖ file ID (8 bytes) |
   | `02` | album (collection) | `02` ‖ 7 zero bytes ‖ collection ID |
   | `03` | album membership | `03` ‖ collection ID (7 bytes) ‖ file ID |
   | `04` | uploaded object (bytes, before the 2026-10-30 change) | `04` ‖ 7 zero bytes ‖ object number |

   The proxy picks new numbers: **milliseconds since 1970 × 2048 + random 0–2047**. They stay
   below 2^53 (JavaScript's exact-integer limit) until about 2109, and they increase with
   creation time, which Ente's web client relies on (album sort by creation time compares IDs,
   `web/packages/new/photos/services/collection-summary.ts`). A collision makes Odin refuse
   the create; nothing is overwritten. The number is read straight back out of the GUID when
   listing. A soft-deleted Odin file loses its uniqueId, so the photo and album IDs also go
   in tags.

2. **A photo is one Homebase file, the Homebase pattern** (2026-11-02; chat-kmp's model).
   The web app encrypts the media in Homebase's format, as every Homebase client does, instead
   of Ente's: the original is the payload (`original`, with its real content type), with
   WebP thumbnails at 320/640/1080/1600 px (never above the source) and a 20 px inline preview,
   made from the image itself (or, for videos, live photos and HEIC, from Ente's thumbnail).
   Ente's record of the photo, still Ente-encrypted, is the file's content; its file and
   thumbnail `decryptionHeader` say `homebase-v1` so Ente knows the bytes have no Ente
   encryption.
   - Client, Homebase mode only: one multipart `POST /homebase/files` with the record, the
     thumbnails and the original (`upload-service.ts` `uploadToHomebase`, `remote.ts`
     `postHomebaseEnteFile`), instead of Ente's upload URLs, byte `PUT`s and `POST /files`.
     Reads: `download-core.ts` `downloadHomebasePhoto`.
   - Upload: 2 calls (the photo, its membership). Thumbnail: 1 call. Original: 1 call.
   - Downloads send X-Auth-Token (`web/packages/base/file-download.ts`).

3. **No credentials in URLs, ever.** The X-Auth-Token holds the drive key, which can't be
   revoked. So no presigned-style URLs and no `?token=`: credentials only in the header.

4. **Albums (option A): mirror Ente's model.** One Odin file per photo record (type `01`),
   and one small file per album membership (type `03`): album X's copy of the photo's key,
   tagged with the album and the photo.
   - Album sync ("what changed since T"): 2 calls (memberships, then their photo records).
   - Edit a photo: 1 call. Add to album: 1 call per photo.
   - Remove from album: soft-delete the membership. Odin's tombstone keeps tags and groupId,
     so sync can report the removal (checked: `DriveStorageServiceBase.WriteDeletedFileHeader`).

5. **The call rule, changed.** Each handler makes **a fixed, small number of Odin calls (at
   most 2)**, or **one per item** for batch requests. Still no state, caching, retries, or
   lookups kept between requests. This replaces "exactly one Odin call per handler".

Logout answers 200 with no Odin call (decided). Still open: public links and sharing with
other identities (left as 501).

## Design

- **Stateless.** Each request's X-Auth-Token is base64url of
  `{"v":1,"identity","token","ss","dk"}`; the proxy decodes it, calls
  `https://<identity>/api/v2/...` with `Authorization: Bearer <token>`, and stores nothing.
  No caching, sessions, cookies, retries, queues, database or file writes.
- **At most two Odin calls per handler** (decision 5), with Odin's encryption as part of
  each: shared-secret envelopes, and zkDrive files (a file key locked with the drive key).
  Ente's own encrypted data passes through untouched.
- **Ente's shapes**: 401 `{"error":"missing token"}` / `{"error":"invalid token"}` as museum's
  auth middleware; 403 `{}` for key attributes already set, as museum; 501 in Ente's ApiError
  shape. Homebase failures other than 401/403 are 502 `HOMEBASE_ERROR`.
- **Ente's drive**: the alias, fixed uniqueIds and file types are in `EnteDrive.cs` (the alias
  and key attributes file from `contract/ente-homebase.json`, see CONTRACT.md); IDs ↔ uniqueIds in
  `EnteIds.cs`. The password file is never touched.
- **Middleware**: X-Request-Id (kept if it's a plain token, else new), request logging
  (method, path, status, duration), CORS (`ALLOWED_ORIGINS`, default http://localhost:3020).
  ASP.NET's and HttpClient's own request logs are turned down to Warning, since they include
  URLs and query strings.

## How to run

```sh
# in this repo
~/.dotnet/dotnet run --project src/EnteHomebaseProxy     # http://localhost:8095
(cd ../ente/web && npm run dev:photos-homebase)          # the web app (homebase-ente), :3020
~/.dotnet/dotnet test                                    # offline tests; live ones skip
docker compose up --build                                # the same, in Docker
```

.NET 10 (SDK 10.0.402) is installed in `~/.dotnet`, not on the default PATH; `global.json`
pins it.
