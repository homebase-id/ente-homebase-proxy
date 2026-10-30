#!/usr/bin/env node
// One-time YouAuth consent for the proxy's live tests: signs the "Ente Photos"
// app in on a Homebase identity, as a client named "Ente proxy tests", and
// saves the credentials to ../.credentials.json (gitignored, mode 600).
// Adapted from the Immich Homebase client's scripts/consent.mjs.
//
// Usage (from proxy/):
//   node --disable-warning=MODULE_TYPELESS_PACKAGE_JSON scripts/consent.mjs [identity]
// Default identity: frodo.baggins.demo.rocks. Open the printed URL, log in as
// the owner and approve Ente Photos.
//
// Ente's app registration and drive come from web/packages/homebase/constants.ts,
// the same values the web app signs in with.
//
// Formats (odin-core): authorize params in YouAuthAuthorizeRequest.cs; the
// exchange secret is HKDF-SHA256(ECDH X, 48 bytes zero-padded, salt), 16 bytes,
// no info (docs/youauth-unified-authorization.md); aes-gcm token fields use the
// first 12 bytes of the IV; zkAppKey is ZkSeal v1 (ZkSeal.cs); the drive key is
// a zkLock on GET /api/v2/auth/context (docs/zero-knowledge-drive-api-plan.md 3.5).
//
// Prints no secret: only the identity, field names, lengths and the drive alias.

import crypto from "node:crypto";
import { writeFileSync } from "node:fs";
import http from "node:http";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import zlib from "node:zlib";
import {
    enteApp,
    enteDrive,
} from "../../web/packages/homebase/constants.ts";

const IDENTITY = process.argv[2] ?? "frodo.baggins.demo.rocks";
const PORT = 7778;
const REDIRECT_URI = `http://localhost:${PORT}/callback`;
const CLIENT_NAME = "Ente proxy tests";
const CREDENTIALS_FILE = join(
    dirname(fileURLToPath(import.meta.url)),
    "..",
    ".credentials.json",
);

const b64 = (buf) => Buffer.from(buf).toString("base64");
const b64url = (buf) => Buffer.from(buf).toString("base64url");
const fromB64 = (s) =>
    Buffer.from(s.replace(/-/g, "+").replace(/_/g, "/"), "base64");

const ecdhX = (privateKey, publicKey) => {
    const x = crypto.diffieHellman({ privateKey, publicKey });
    return Buffer.concat([Buffer.alloc(48 - x.length), x]); // odin-core #1728
};

const hkdf16 = (ikm, salt, info) =>
    Buffer.from(crypto.hkdfSync("sha256", ikm, salt, info, 16));

const gcmOpen = (key, iv12, cipherAndTag) => {
    const decipher = crypto.createDecipheriv("aes-128-gcm", key, iv12);
    decipher.setAuthTag(cipherAndTag.subarray(-16));
    return Buffer.concat([
        decipher.update(cipherAndTag.subarray(0, -16)),
        decipher.final(),
    ]);
};

const cbcOpen = (key, iv, cipher) => {
    const decipher = crypto.createDecipheriv("aes-128-cbc", key, iv);
    return Buffer.concat([decipher.update(cipher), decipher.final()]);
};

const rawPoint = (publicKey) => {
    const jwk = publicKey.export({ format: "jwk" });
    return Buffer.concat([Buffer.from([0x04]), fromB64(jwk.x), fromB64(jwk.y)]);
};

// ZkSeal v1: 0x01 ‖ key id (4, BE) ‖ throwaway public key (97) ‖ IV (12) ‖ GCM ciphertext ‖ tag
const zkUnseal = (sealedB64, privateKey, publicKey) => {
    const sealed = fromB64(sealedB64);
    if (sealed[0] !== 1 || sealed[5] !== 0x04) {
        throw new Error("zkAppKey is not a ZkSeal v1 value");
    }
    if (sealed.readUInt32BE(1) !== zlib.crc32(rawPoint(publicKey))) {
        throw new Error("zkAppKey is sealed to a different key");
    }
    const point = sealed.subarray(5, 102);
    const throwaway = crypto.createPublicKey({
        key: {
            kty: "EC",
            crv: "P-384",
            x: b64url(point.subarray(1, 49)),
            y: b64url(point.subarray(49, 97)),
        },
        format: "jwk",
    });
    const key = hkdf16(
        ecdhX(privateKey, throwaway),
        Buffer.alloc(0),
        Buffer.from("homebase-zk-seal"),
    );
    return gcmOpen(key, sealed.subarray(102, 114), sealed.subarray(114));
};

const { privateKey, publicKey } = crypto.generateKeyPairSync("ec", {
    namedCurve: "P-384",
});
const { kty, crv, x, y } = publicKey.export({ format: "jwk" });
const state = b64url(crypto.randomBytes(16));

const permissionRequest = {
    n: enteApp.appName,
    appId: enteApp.appId,
    as: enteApp.appSlug,
    fn: CLIENT_NAME,
    d: JSON.stringify(
        enteApp.drives.map((d) => ({
            a: d.alias,
            t: d.type,
            n: d.name,
            d: d.description,
            p: d.permission,
            ds: d.driveSlug,
            ts: d.driveTypeSlug,
            sm: d.sharingModel,
        })),
    ),
    return: "backend-will-decide",
};

const authorizeUrl = new URL(
    `https://${IDENTITY}/api/owner/v1/youauth/authorize`,
);
for (const [k, v] of Object.entries({
    client_id: enteApp.appId,
    client_type: "app",
    client_info: CLIENT_NAME,
    redirect_uri: REDIRECT_URI,
    permission_request: JSON.stringify(permissionRequest),
    public_key: b64url(JSON.stringify({ kty, crv, x, y })),
    state,
    cipher: "aes-gcm",
    zk_app_key: "1",
})) {
    authorizeUrl.searchParams.set(k, v);
}

const findGrants = (node, out = []) => {
    if (Array.isArray(node)) node.forEach((n) => findGrants(n, out));
    else if (node && typeof node === "object") {
        if ("permissionedDrive" in node) out.push(node);
        Object.values(node).forEach((n) => findGrants(n, out));
    }
    return out;
};

const sameGuid = (a, b) =>
    !!a &&
    !!b &&
    a.replaceAll("-", "").toLowerCase() === b.replaceAll("-", "").toLowerCase();

const finish = async (params) => {
    if (params.get("error")) throw new Error(`identity said: ${params.get("error")}`);
    if (params.get("state") !== state) throw new Error("state mismatch");

    // Trust the identity we sent the owner to, not one the callback names.
    const remote = crypto.createPublicKey({
        key: JSON.parse(fromB64(params.get("public_key")).toString()),
        format: "jwk",
    });
    const secret = hkdf16(
        ecdhX(privateKey, remote),
        fromB64(params.get("salt")),
        Buffer.alloc(0),
    );

    const tokenRes = await fetch(
        `https://${IDENTITY}/api/owner/v1/youauth/token`,
        {
            method: "POST",
            headers: { "content-type": "application/json" },
            body: JSON.stringify({
                secret_digest: b64(
                    crypto.createHash("sha256").update(secret).digest(),
                ),
            }),
        },
    );
    if (!tokenRes.ok) throw new Error(`token exchange: HTTP ${tokenRes.status}`);
    const token = await tokenRes.json();
    if ((token.cipher ?? "aes-cbc") !== "aes-gcm") {
        throw new Error("asked for aes-gcm, got aes-cbc");
    }
    if (!token.zkAppKey) throw new Error("no zkAppKey: odin-core too old?");

    const open = (iv, cipher) =>
        gcmOpen(secret, fromB64(iv).subarray(0, 12), fromB64(cipher));
    const sharedSecret = open(
        token.base64SharedSecretIv,
        token.base64SharedSecretCipher,
    );
    const clientAuthToken = open(
        token.base64ClientAuthTokenIv,
        token.base64ClientAuthTokenCipher,
    );
    const appZkKey = zkUnseal(token.zkAppKey, privateKey, publicKey);

    // Check the chain before saving: Ente's drive grant opens with appZkKey.
    const ctxRes = await fetch(`https://${IDENTITY}/api/v2/auth/context`, {
        headers: { authorization: `Bearer ${b64(clientAuthToken)}` },
    });
    if (!ctxRes.ok) throw new Error(`auth/context: HTTP ${ctxRes.status}`);
    let ctx = await ctxRes.json();
    if (ctx.iv && ctx.data) {
        ctx = JSON.parse(
            cbcOpen(sharedSecret, fromB64(ctx.iv), fromB64(ctx.data)).toString(),
        );
    }
    const grant = findGrants(ctx).find((g) =>
        sameGuid(g.permissionedDrive?.drive?.alias, enteDrive.alias),
    );
    if (!grant?.zkAppKeyEncryptedStorageKey) {
        throw new Error("no zero-knowledge grant for Ente's drive");
    }
    const lock = fromB64(grant.zkAppKeyEncryptedStorageKey);
    const driveKey = gcmOpen(appZkKey, lock.subarray(0, 12), lock.subarray(12));
    console.log(
        `drive key for ${enteDrive.alias} opened (${driveKey.length} bytes)`,
    );

    writeFileSync(
        CREDENTIALS_FILE,
        JSON.stringify(
            {
                identity: IDENTITY,
                appId: enteApp.appId,
                clientName: CLIENT_NAME,
                drive: { alias: enteDrive.alias, type: enteDrive.type },
                clientAuthToken: b64(clientAuthToken),
                sharedSecret: b64(sharedSecret),
                appZkKey: b64(appZkKey),
                createdAt: new Date().toISOString(),
            },
            null,
            2,
        ),
        { mode: 0o600 },
    );
    console.log(`credentials saved to ${CREDENTIALS_FILE} (mode 600)`);
};

const server = http.createServer(async (req, res) => {
    const url = new URL(req.url, `http://localhost:${PORT}`);
    if (url.pathname !== "/callback") return res.writeHead(404).end();
    try {
        await finish(url.searchParams);
        res.writeHead(200, { "content-type": "text/plain" }).end(
            "Ente proxy tests: signed in. You can close this tab.",
        );
    } catch (error) {
        console.error(`FAILED: ${error.message}`);
        res.writeHead(500, { "content-type": "text/plain" }).end(
            `Ente proxy tests: sign-in failed: ${error.message}`,
        );
    } finally {
        server.close();
    }
});

server.listen(PORT, () => {
    console.log(
        `Open this URL, log in as ${IDENTITY}, and approve Ente Photos ` +
            `("${CLIENT_NAME}"):\n\n${authorizeUrl}\n`,
    );
});
