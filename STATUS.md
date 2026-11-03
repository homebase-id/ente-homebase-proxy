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
the one it made. Once the web app's key setup creates your real one, it skips. The photo test
(`LivePhotoTests`) makes an album and a photo through the proxy, plays the web app (it seals
them with the drive key, which the proxy never gets), and deletes the three files afterwards. If Homebase
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

**Zero knowledge: works** (2026-11-02, same setup). Everything on Ente's drive is sealed by the
web app the way chat-kmp seals it, and the proxy has no key that opens any of it:

1. The old test data was deleted first (Todd approved): 20 files in the old formats, keeping
   only the hidden password file.
2. Sign in (first time): the web app sets up Ente's keys and stores the key attributes sealed.
3. Upload into Uncategorized: the album is created sealed, then the photo, in one request.
4. The thumbnail shows, and still shows after a reload.
5. **A fresh browser** signs in, opens the sealed key attributes, and shows and opens the photo.
6. Read straight from Odin, as any Homebase client would:
   - the album, the photo and the key attributes are `zkDrive` files that open with the drive key;
   - the photo's payload IV differs from its file IV;
   - its thumbnails are 320×240, 640×480, 1080×810 and 1600×1200;
   - the original's SHA-256 matches the source;
   - the membership is unencrypted and empty.
7. Every X-Auth-Token the web app sent was `{v, identity, token, ss}`: no drive key.

The test photo and its album (Uncategorized) are in your Ente library on frodo.

## What works

| Route | Odin calls | Notes |
|---|---|---|
| `PUT /users/attributes`, `GET /users/session-validity/v2` | 1 | Key attributes file, sealed by the web app; 403 if set, never overwritten |
| `GET /users/details/v2` | 0 | The identity as an Ente user; a 1 TiB "homebase" plan. TODO: real usage |
| `POST /users/logout` | 0 | Nothing to do on Homebase |
| `POST /collections`, `GET /collections/{id}` | 1 | An album, sealed by the web app; a deleted one says `isDeleted` |
| `GET /collections/v2` | 1 | Every album, deleted ones as `isDeleted`; each album's updationTime is *now* (see below) |
| `GET /collections/v2/diff` | 2 | The album's memberships (tombstones too), then their photos. A photo is in it when its membership or its own details changed; a removed, moved, trashed or deleted one is `isDeleted` |
| `POST /collections/add-files` | 1 + 1 per photo | Photos into another album: the album's header, then a membership per photo; one already in the album is left as is; Ente's file keys are ignored |
| `POST /collections/v3/remove-files` | 2 | The memberships, then a soft delete of them |
| `POST /collections/move-files` | 3 + 1 per photo | add-files to one album, remove-files from the other |
| `POST /collections/rename`, `PUT /collections/magic-metadata`, `PUT /collections/public-magic-metadata` | 2 | The album's details sealed again by the web app (same key, new IV): its header, then an update |
| `DELETE /collections/v3/{id}?keepFiles` | 3 (+ trash) | The album marked removed (archivalStatus 2). keepFiles: 409 `COLLECTION_NOT_EMPTY` unless empty; otherwise its photos are trashed |
| `PUT /files/magic-metadata`, `PUT /files/public-magic-metadata` | 1 + 1 per photo | Each photo's details sealed again by the web app (hide, archive, date, caption, location): the photos, then an update each; payloads untouched |
| `POST /files/trash` | 4 + 1 per photo | Out of every album (memberships soft-deleted), a trash entry (7212: when, and from which album), and the photo marked removed; its bytes and details stay |
| `GET /trash/v2/diff` | 2 | The trash entries (tombstones too), then the photos still in the trash; a closed entry is restored or deleted by the archivalStatus it was closed with; deleteBy is 30 days after trashing |
| `POST /collections/restore-files` | 6 + 1 per photo | A membership each, the photo no longer removed, its trash entry closed as restored |
| `POST /trash/delete`, `POST /trash/empty` | 5 | The photos hard-deleted (bytes and all), their trash entries closed as deleted |
| `POST /homebase/files` | 2 | A photo in one request (decision 2): one Homebase file, then its membership; up to 1 GiB, streamed; stored as sent |
| `GET /homebase/files/{id}/thumb?width&height` | 1 | The thumbnail covering that size, as stored (the web app decrypts it) |
| `GET /homebase/files/{id}/original` | 1 | The original, as stored, streamed (the web app decrypts it) |
| `/remote-store/feature-flags`, `/contacts/diff`, `/user-entity/entity/diff`, `/collection-actions/pending-remove` | 0 | Empty: nothing of that kind can exist yet |

Plus: Odin's encryption checked against the TypeScript and Node vectors; X-Auth-Token parsing;
every Ente route in the five groups mapped and none mapped twice; `/health`; `/version` (the proxy's and the web app's commits); X-Request-Id; CORS
(`Content-MD5` allowed for uploads); logs hold no token, query or body; Docker image.

73 tests, all passing with `HOMEBASE_CREDENTIALS` set (2026-11-03), except the key endpoint test,
which skips while a real key attributes file is on the drive. `LiveLibraryTests` takes one photo
through all of the above on frodo (another album, an edit, a rename, removed, moved, trashed,
restored, deleted for good, its albums deleted) and cleans up after itself.

## Worth knowing

- **Album updationTime is always now.** Ente bumps an album when its photos change, and the
  client only syncs an album whose time moved. Bumping on upload would be a third call, so
  instead every pull syncs every album (each sync returns only changes). The sync looks 10
  minutes back from sinceTime, since the client may store the proxy's clock while changes carry
  Odin's; repeats are harmless.
- **Zero knowledge, chat-kmp's way** (2026-11-02). The web app seals everything it keeps
  on Ente's drive: each file gets a random key, locked with the drive key in a `zkDrive` key
  header, and its JSON is encrypted with that key. Photos' originals and thumbnails use the
  same key with the payload's own random IV. The proxy only maps Ente's API to Odin's: it
  stores and returns what's sealed as it is (`Odin/SealedRecord.cs`), and has no drive key.
  Other Homebase apps that can read the drive open everything like any other Homebase file.
- **No Ente encryption on what's stored**, with one exception: the key attributes, which are
  Ente's master key locked with the hidden Ente password. Ente's session needs a master key,
  but in Homebase mode it protects nothing that's stored (no album keys, no file keys; people
  and places aren't kept yet). It's a key, sealed again by Homebase, not data.
- **The inline preview isn't encrypted.** Homebase keeps every file's tiny `previewThumbnail`
  (20 px, under 1 KB) in its metadata as is, for every app (odin-js `encryptMetaData`
  encrypts only the content); Ente's photos have one too.
- **Big videos are encrypted in browser memory.** WebCrypto's AES-CBC doesn't stream, so the
  web app holds the whole file and its ciphertext at once (up to the 1 GiB limit).
- **Photo sizes are Odin's**: `info.fileSize` and `thumbSize` are the stored (encrypted)
  bytes, up to 16 more than the plain file.
- **Older formats are gone.** Photos stored before 2026-11-02 (object files, Ente's ciphertext,
  derived keys) no longer read; the test data in them was deleted.
- **No paging yet**: an album sync reads at most 1000 memberships, the album list 1000 albums,
  the trash 1000 entries.
- **Removed is archivalStatus 2** (Odin's "removed"), on a photo in the trash and on a deleted
  album; other readers of the drive (chat-kmp) skip it. A soft delete can't stand in for the
  trash: Odin's tombstone drops the content and payloads, so nothing could be restored.
- **Edits are sealed by the browser.** The proxy can't open a record, so an edit (album name or
  settings, a photo's visibility, date, caption or location) arrives as the whole record sealed
  again with the file's own key and a new IV; the proxy writes it as it is. The last write wins
  (no version check).
- **A deleted album's file stays** (marked removed), sealed, so a trashed photo from it still
  opens. Ente's server also keeps a deleted collection.
- **Security (prototype)**: the web app keeps the X-Auth-Token (the sign-in, no drive key)
  where Ente keeps its auth token (localStorage and IndexedDB, unencrypted).
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
   | `04` | uploaded object (old format, no longer used) | `04` ‖ 7 zero bytes ‖ object number |

   The proxy picks new numbers: **milliseconds since 1970 × 2048 + random 0–2047**. They stay
   below 2^53 (JavaScript's exact-integer limit) until about 2109, and they increase with
   creation time, which Ente's web client relies on (album sort by creation time compares IDs,
   `web/packages/new/photos/services/collection-summary.ts`). A collision makes Odin refuse
   the create; nothing is overwritten. The number is read straight back out of the GUID when
   listing. A soft-deleted Odin file loses its uniqueId, so the photo and album IDs also go
   in tags.

2. **A photo is one Homebase file, sealed the way chat-kmp seals one** (2026-11-02). The web
   app gives the photo a random key, locks it with the drive key, and seals the photo's
   details (Ente's metadata and magic metadata, as plain JSON) with it. The original is the
   payload (`original`, with its real content type), encrypted with the same key and its own
   random IV. Its WebP thumbnails are at 320/640/1080/1600 px (never above the source), with
   the payload's IV, plus a 20 px inline preview. They're made from the image itself (or, for
   videos, live photos and HEIC, from Ente's thumbnail). There's no Ente encryption on any of
   it. Ente's record of the photo, as the proxy returns it, has blank Ente fields: the sealed
   details are `homebase`, and the file and thumbnail `decryptionHeader` are `homebase-v2:` and
   the payload IV.
   - Client, Homebase mode only: one multipart `POST /homebase/files` (`upload-service.ts`
     `uploadToHomebase`, `remote.ts` `postHomebaseEnteFile`), instead of Ente's upload URLs,
     byte `PUT`s and `POST /files`. Reads: `ente-media` `file.ts` (the details),
     `download-core.ts` `downloadHomebasePhoto` (the bytes).
   - Upload: 2 calls (the photo, its membership). Thumbnail: 1. Original: 1.
   - Downloads send X-Auth-Token (`web/packages/base/file-download.ts`).
   - Unlike chat-kmp, which strips EXIF from media it sends to others, the original keeps its
     EXIF: it's the owner's own library.

3. **No credentials in URLs, ever.** The X-Auth-Token signs in to the identity. So no
   presigned-style URLs and no `?token=`: credentials only in the header.

4. **Albums (option A): mirror Ente's model.** One Odin file per photo (type `01`), and one
   small file per album membership (type `03`), unencrypted and empty (there are no per-album
   keys), tagged with the album and the photo.
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
  `{"v":2,"identity","token","ss"}`; the proxy decodes it, calls
  `https://<identity>/api/v2/...` with `Authorization: Bearer <token>`, and stores nothing.
  No caching, sessions, cookies, retries, queues, database or file writes.
- **A fixed, small number of Odin calls per handler** (decision 5), with Odin's transport
  encryption (shared-secret envelopes) as part of each. No storage encryption: what's kept is
  sealed by the web app and passes through unopened.
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
