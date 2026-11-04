// WebAuthn bridge of the passkeys (ADR 0168). The OS builds the options and verifies every answer
// in C#; this module only turns base64url strings into the buffers navigator.credentials wants and
// back, and verifies a signature with Web Crypto (.NET in WebAssembly has no ECDSA or RSA).

/** @param {string} text base64url text. @returns {Uint8Array} */
function fromBase64Url(text) {
    const base64 = text.replace(/-/g, "+").replace(/_/g, "/");
    const padded = base64 + "=".repeat((4 - (base64.length % 4)) % 4);
    const binary = atob(padded);
    const bytes = new Uint8Array(binary.length);
    for (let index = 0; index < binary.length; index++) {
        bytes[index] = binary.charCodeAt(index);
    }
    return bytes;
}

/** @param {ArrayBuffer|Uint8Array|null|undefined} buffer @returns {string|null} base64url text. */
function toBase64Url(buffer) {
    if (!buffer) {
        return null;
    }
    const bytes = buffer instanceof Uint8Array ? buffer : new Uint8Array(buffer);
    let binary = "";
    for (let index = 0; index < bytes.length; index++) {
        binary += String.fromCharCode(bytes[index]);
    }
    return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

/** Turns a DOMException of WebAuthn into an error whose message the C# side recognizes. */
function rethrow(error) {
    const name = error && error.name ? error.name : "Error";
    throw new Error(`webauthn:${name}:${error && error.message ? error.message : ""}`);
}

/**
 * Whether the page can use passkeys, and its origin and relying party id.
 * @returns {{ isSupported: boolean, origin: string, rpId: string }}
 */
export function getEnvironment() {
    const isSupported = !!(window.isSecureContext
        && window.PublicKeyCredential
        && navigator.credentials
        && typeof navigator.credentials.create === "function");
    return { isSupported, origin: location.origin, rpId: location.hostname };
}

/**
 * navigator.credentials.create with the options the OS built.
 * @param {{ challenge: string, rpId: string, rpName: string, userHandle: string, userName: string,
 *           userDisplayName: string, excludeCredentialIds: string[], prfSalt: string }} options
 */
export async function create(options) {
    let credential;
    try {
        credential = await navigator.credentials.create({
            publicKey: {
                challenge: fromBase64Url(options.challenge),
                rp: { id: options.rpId, name: options.rpName },
                user: {
                    id: fromBase64Url(options.userHandle),
                    name: options.userName,
                    displayName: options.userDisplayName
                },
                pubKeyCredParams: [
                    { type: "public-key", alg: -7 },
                    { type: "public-key", alg: -257 }
                ],
                excludeCredentials: options.excludeCredentialIds.map(id => ({ type: "public-key", id: fromBase64Url(id) })),
                authenticatorSelection: { residentKey: "preferred", userVerification: "preferred" },
                attestation: "none",
                timeout: 120000,
                extensions: { prf: { eval: { first: fromBase64Url(options.prfSalt) } } }
            }
        });
    } catch (error) {
        rethrow(error);
    }

    const response = credential.response;
    const extensions = credential.getClientExtensionResults ? credential.getClientExtensionResults() : {};
    const prf = extensions.prf || {};
    return {
        credentialId: toBase64Url(credential.rawId),
        clientDataJson: toBase64Url(response.clientDataJSON),
        authenticatorData: toBase64Url(response.getAuthenticatorData ? response.getAuthenticatorData() : null),
        publicKey: toBase64Url(response.getPublicKey ? response.getPublicKey() : null),
        algorithm: response.getPublicKeyAlgorithm ? response.getPublicKeyAlgorithm() : 0,
        prfEnabled: prf.enabled === true || !!(prf.results && prf.results.first),
        prfOutput: prf.results && prf.results.first ? toBase64Url(prf.results.first) : null
    };
}

/**
 * navigator.credentials.get with the options the OS built.
 * @param {{ challenge: string, rpId: string, allowCredentials: { id: string, prfSalt: string|null }[] }} options
 */
export async function get(options) {
    const evalByCredential = {};
    for (const allowed of options.allowCredentials) {
        if (allowed.prfSalt) {
            evalByCredential[allowed.id] = { first: fromBase64Url(allowed.prfSalt) };
        }
    }

    const publicKey = {
        challenge: fromBase64Url(options.challenge),
        rpId: options.rpId,
        allowCredentials: options.allowCredentials.map(allowed => ({ type: "public-key", id: fromBase64Url(allowed.id) })),
        userVerification: "preferred",
        timeout: 120000
    };
    if (Object.keys(evalByCredential).length > 0) {
        publicKey.extensions = { prf: { evalByCredential } };
    }

    let credential;
    try {
        credential = await navigator.credentials.get({ publicKey });
    } catch (error) {
        rethrow(error);
    }

    const response = credential.response;
    const extensions = credential.getClientExtensionResults ? credential.getClientExtensionResults() : {};
    const results = extensions.prf && extensions.prf.results;
    return {
        credentialId: toBase64Url(credential.rawId),
        clientDataJson: toBase64Url(response.clientDataJSON),
        authenticatorData: toBase64Url(response.authenticatorData),
        signature: toBase64Url(response.signature),
        prfOutput: results && results.first ? toBase64Url(results.first) : null
    };
}

/**
 * Verifies a WebAuthn signature with a stored SubjectPublicKeyInfo. ECDSA signatures arrive already
 * converted from DER to r||s by the C# side.
 * @param {number} algorithm COSE algorithm (-7 ES256, -257 RS256).
 * @param {string} publicKey base64url SPKI.
 * @param {string} data base64url signed bytes.
 * @param {string} signature base64url signature.
 * @returns {Promise<boolean>}
 */
export async function verify(algorithm, publicKey, data, signature) {
    const parameters = algorithm === -7
        ? [{ name: "ECDSA", namedCurve: "P-256" }, { name: "ECDSA", hash: "SHA-256" }]
        : [{ name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" }, { name: "RSASSA-PKCS1-v1_5" }];
    try {
        const key = await crypto.subtle.importKey("spki", fromBase64Url(publicKey), parameters[0], false, ["verify"]);
        return await crypto.subtle.verify(parameters[1], key, fromBase64Url(signature), fromBase64Url(data));
    } catch {
        return false;
    }
}
