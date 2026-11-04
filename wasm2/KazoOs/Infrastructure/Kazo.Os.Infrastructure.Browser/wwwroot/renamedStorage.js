// Browser storage of the rename migration (ADR 0230): what C# cannot reach by itself.
//
// The decisions (which database, which key, which new name) are taken in C#
// (Rename/RenamedStorageMigration.cs); this module only lists and moves what the browser holds.
// A database is copied with the structured clone of IndexedDB itself, so values JSON cannot carry
// (FileSystemDirectoryHandle of kazoos-host-folders, Blob, Date) arrive unchanged.

function requestResult(request) {
    return new Promise((resolve, reject) => {
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error ?? new Error("IndexedDB request failed."));
    });
}

function storageArea(area) {
    return area === "session" ? globalThis.sessionStorage : globalThis.localStorage;
}

/** Lists the IndexedDB databases of the origin. */
export async function listDatabases() {
    const databases = await indexedDB.databases();
    return databases.map(database => database.name).filter(name => typeof name === "string");
}

/** Reads a whole database: its version, and per store the schema and every (key, value) pair. */
async function readDatabase(name) {
    const database = await requestResult(indexedDB.open(name));
    try {
        const names = Array.from(database.objectStoreNames);
        const stores = names.map(storeName => ({ name: storeName, records: [] }));
        if (names.length > 0) {
            const transaction = database.transaction(names, "readonly");
            for (const store of stores) {
                const source = transaction.objectStore(store.name);
                store.keyPath = source.keyPath;
                store.autoIncrement = source.autoIncrement;
                store.indexes = Array.from(source.indexNames).map(indexName => {
                    const index = source.index(indexName);
                    return { name: index.name, keyPath: index.keyPath, unique: index.unique, multiEntry: index.multiEntry };
                });
                const [keys, values] = await Promise.all([
                    requestResult(source.getAllKeys()),
                    requestResult(source.getAll())
                ]);
                store.records = keys.map((key, i) => ({ key, value: values[i] }));
            }
        }

        return { version: database.version, stores };
    } finally {
        database.close();
    }
}

/**
 * Copies database `source` to `target` at the same version: stores, indexes and records, all
 * written inside the upgrade transaction that creates `target`, so an interrupted copy leaves no
 * `target` at all. Returns false, copying nothing, when `target` already exists.
 */
export async function copyDatabase(source, target) {
    const existing = await listDatabases();
    if (existing.includes(target)) {
        return false;
    }
    if (!existing.includes(source)) {
        throw new Error(`NotFoundError: database '${source}' does not exist.`);
    }

    const snapshot = await readDatabase(source);
    const request = indexedDB.open(target, snapshot.version);
    request.onupgradeneeded = () => {
        const database = request.result;
        for (const store of snapshot.stores) {
            const options = { autoIncrement: store.autoIncrement === true };
            if (store.keyPath !== null && store.keyPath !== undefined) {
                options.keyPath = store.keyPath;
            }
            const created = database.createObjectStore(store.name, options);
            for (const index of store.indexes) {
                created.createIndex(index.name, index.keyPath, { unique: index.unique, multiEntry: index.multiEntry });
            }
            for (const record of store.records) {
                if (created.keyPath === null) {
                    created.put(record.value, record.key);
                } else {
                    created.put(record.value);
                }
            }
        }
    };
    const database = await requestResult(request);
    database.close();
    return true;
}

/** Deletes a database. */
export async function deleteDatabase(name) {
    await requestResult(indexedDB.deleteDatabase(name));
}

/** Reads every [key, value] pair of localStorage ("local") or sessionStorage ("session"). */
export function readStorage(area) {
    const storage = storageArea(area);
    const entries = [];
    for (let i = 0; i < storage.length; i++) {
        const key = storage.key(i);
        entries.push([key, storage.getItem(key) ?? ""]);
    }
    return entries;
}

/** Writes one key of localStorage or sessionStorage. */
export function writeStorage(area, key, value) {
    storageArea(area).setItem(key, value);
}

/** Removes one key of localStorage or sessionStorage. */
export function removeStorage(area, key) {
    storageArea(area).removeItem(key);
}

/** Lists the Cache Storage caches of the origin (none where the API is missing). */
export async function cacheNames() {
    return globalThis.caches ? await globalThis.caches.keys() : [];
}

/** Deletes one Cache Storage cache. */
export async function deleteCache(name) {
    if (globalThis.caches) {
        await globalThis.caches.delete(name);
    }
}
