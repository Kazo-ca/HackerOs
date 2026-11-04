/**
 * Executes native PBKDF2-HMAC-SHA256 key derivation via browser Web Crypto API (crypto.subtle).
 * 
 * @param {string} password Plaintext password to hash.
 * @param {Uint8Array|Array<number>} saltBytes Salt byte array.
 * @param {number} iterations PBKDF2 iteration work factor.
 * @param {number} keyLengthBytes Output verifier length in bytes.
 * @returns {Promise<Uint8Array>} Derived key bytes.
 */
export async function derivePbkdf2Key(password, saltBytes, iterations, keyLengthBytes) {
    if (!password) {
        throw new Error("Password cannot be empty.");
    }
    const encoder = new TextEncoder();
    const passwordBytes = encoder.encode(password);
    const keyMaterial = await crypto.subtle.importKey(
        "raw",
        passwordBytes,
        "PBKDF2",
        false,
        ["deriveBits"]
    );
    const salt = saltBytes instanceof Uint8Array ? saltBytes : new Uint8Array(saltBytes);
    const derivedBits = await crypto.subtle.deriveBits(
        {
            name: "PBKDF2",
            salt: salt,
            iterations: iterations,
            hash: "SHA-256"
        },
        keyMaterial,
        keyLengthBytes * 8
    );
    return new Uint8Array(derivedBits);
}

/**
 * Seals bytes with AES-256-GCM (ADR 0100, user keyring). The result is the ciphertext followed by
 * the 16-byte authentication tag, the layout Web Crypto produces and .NET's AesGcm reads.
 *
 * @param {Uint8Array|Array<number>} keyBytes 32-byte raw AES key.
 * @param {Uint8Array|Array<number>} nonceBytes 12-byte nonce, never reused with the same key.
 * @param {Uint8Array|Array<number>} plaintextBytes Bytes to seal.
 * @returns {Promise<Uint8Array>} Ciphertext then tag.
 */
export async function aesGcmEncrypt(keyBytes, nonceBytes, plaintextBytes) {
    const key = await importAesKey(keyBytes, "encrypt");
    const sealed = await crypto.subtle.encrypt(
        { name: "AES-GCM", iv: toBytes(nonceBytes), tagLength: 128 },
        key,
        toBytes(plaintextBytes));
    return new Uint8Array(sealed);
}

/**
 * Opens bytes sealed by aesGcmEncrypt. Returns null instead of throwing when the key is wrong or
 * the data was altered (the tag does not match), so the caller can tell a bad password apart.
 *
 * @param {Uint8Array|Array<number>} keyBytes 32-byte raw AES key.
 * @param {Uint8Array|Array<number>} nonceBytes 12-byte nonce used to seal.
 * @param {Uint8Array|Array<number>} sealedBytes Ciphertext then tag.
 * @returns {Promise<Uint8Array|null>} Plaintext, or null.
 */
export async function aesGcmDecrypt(keyBytes, nonceBytes, sealedBytes) {
    const key = await importAesKey(keyBytes, "decrypt");
    try {
        const plain = await crypto.subtle.decrypt(
            { name: "AES-GCM", iv: toBytes(nonceBytes), tagLength: 128 },
            key,
            toBytes(sealedBytes));
        return new Uint8Array(plain);
    } catch (error) {
        if (error && error.name === "OperationError") {
            return null;
        }
        throw error;
    }
}

function toBytes(value) {
    return value instanceof Uint8Array ? value : new Uint8Array(value);
}

function importAesKey(keyBytes, usage) {
    return crypto.subtle.importKey("raw", toBytes(keyBytes), { name: "AES-GCM" }, false, [usage]);
}
