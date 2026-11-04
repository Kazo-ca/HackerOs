// Browser databases of Kazo OS web sites (ADR 0088).
//
// One IndexedDB database per site, "kazoweb.<app id>", opened by the OS for the site: C# passes
// the schema and each transaction as JSON, this module runs them with the real IndexedDB and
// returns JSON. Every name must start with "kazoweb.", so the OS database stays out of reach.
// Errors are thrown as "<DOMException name>: <message>" so C# can rebuild KazoWebDbException.

const prefix = "kazoweb.";
const connections = new Map();

function requireName(name) {
    if (typeof name !== "string" || !name.startsWith(prefix) || name.length <= prefix.length) {
        throw new Error(`DataError: '${name}' is not a site database name.`);
    }
}

function failure(error, fallback) {
    if (error && error.name && error.message !== undefined) {
        return new Error(`${error.name}: ${error.message}`);
    }
    return new Error(`InvalidStateError: ${fallback}`);
}

function requestResult(request) {
    return new Promise((resolve, reject) => {
        request.onsuccess = () => resolve(request.result);
        request.onerror = (event) => {
            // Handled here: the failure aborts the transaction, which the caller reports.
            event.preventDefault();
            reject(failure(request.error, "IndexedDB request failed."));
        };
    });
}

function completion(transaction) {
    return new Promise((resolve, reject) => {
        transaction.oncomplete = () => resolve();
        transaction.onabort = () => reject(failure(transaction.error, "The transaction was aborted."));
    });
}

function forget(name) {
    const connection = connections.get(name);
    if (connection) {
        connection.close();
        connections.delete(name);
    }
}

function upgrade(database, transaction, schema, oldVersion, newVersion) {
    for (let version = oldVersion + 1; version <= newVersion; version++) {
        const step = (schema.steps || []).find((candidate) => candidate.targetVersion === version);
        if (!step) {
            transaction.abort();
            throw new Error(`InvalidStateError: No migration step reaches version ${version}.`);
        }
        for (const name of step.deleteStores || []) {
            database.deleteObjectStore(name);
        }
        for (const definition of step.createStores || []) {
            const options = { autoIncrement: definition.autoIncrement === true };
            if (definition.keyPath !== undefined && definition.keyPath !== null) {
                options.keyPath = definition.keyPath;
            }
            const store = database.createObjectStore(definition.name, options);
            for (const index of definition.indexes || []) {
                store.createIndex(index.name, index.keyPath, { unique: index.unique === true, multiEntry: index.multiEntry === true });
            }
        }
        for (const change of step.deleteIndexes || []) {
            transaction.objectStore(change.store).deleteIndex(change.index.name);
        }
        for (const change of step.createIndexes || []) {
            const index = change.index;
            transaction.objectStore(change.store).createIndex(index.name, index.keyPath, { unique: index.unique === true, multiEntry: index.multiEntry === true });
        }
    }
}

/** Opens a site database at the schema's version (indexedDB.open + onupgradeneeded). */
export async function open(name, schemaJson) {
    requireName(name);
    const schema = JSON.parse(schemaJson);
    const existing = connections.get(name);
    if (existing && existing.version === schema.version) {
        return;
    }
    forget(name);

    const request = indexedDB.open(name, schema.version);
    request.onupgradeneeded = (event) => upgrade(request.result, request.transaction, schema, event.oldVersion, event.newVersion);
    const database = await requestResult(request);
    // The Diagnostic app or an uninstall deletes the database: let it, the next call reopens it.
    database.onversionchange = () => {
        database.close();
        if (connections.get(name) === database) {
            connections.delete(name);
        }
    };
    connections.set(name, database);
}

function keyRange(range) {
    const lower = range.lower === undefined || range.lower === null ? undefined : JSON.parse(range.lower);
    const upper = range.upper === undefined || range.upper === null ? undefined : JSON.parse(range.upper);
    if (lower !== undefined && upper !== undefined) {
        return IDBKeyRange.bound(lower, upper, range.lowerOpen === true, range.upperOpen === true);
    }
    if (lower !== undefined) {
        return IDBKeyRange.lowerBound(lower, range.lowerOpen === true);
    }
    if (upper !== undefined) {
        return IDBKeyRange.upperBound(upper, range.upperOpen === true);
    }
    throw new Error("DataError: A key range needs at least one bound.");
}

function json(value) {
    return value === undefined ? null : JSON.stringify(value);
}

async function run(transaction, operation) {
    const store = transaction.objectStore(operation.store);
    const source = operation.index ? store.index(operation.index) : store;
    const key = operation.key === undefined || operation.key === null ? undefined : JSON.parse(operation.key);
    const query = operation.range ? keyRange(operation.range) : key;
    const count = operation.count > 0 ? operation.count : undefined;

    switch (operation.kind) {
        case "get":
            return json(await requestResult(source.get(query)));
        case "getAll":
            return json(await requestResult(source.getAll(query, count)));
        case "getAllKeys":
            // Through an index, getAllKeys gives primary keys, as in IndexedDB.
            return json(await requestResult(source.getAllKeys(query, count)));
        case "count":
            return String(await requestResult(query === undefined ? source.count() : source.count(query)));
        case "add":
        case "put": {
            const value = JSON.parse(operation.value);
            const write = operation.kind === "add" ? store.add.bind(store) : store.put.bind(store);
            return json(await requestResult(key === undefined ? write(value) : write(value, key)));
        }
        case "delete":
            await requestResult(store.delete(query));
            return null;
        case "clear":
            await requestResult(store.clear());
            return null;
        default:
            throw new Error(`InvalidStateError: Unknown operation '${operation.kind}'.`);
    }
}

/** Runs one batch in one transaction and returns the JSON array of its results. */
export async function execute(name, requestJson) {
    requireName(name);
    const request = JSON.parse(requestJson);
    const database = connections.get(name);
    if (!database || database.version !== request.version) {
        throw new Error(`InvalidStateError: The database '${name}' is not open at version ${request.version}.`);
    }

    let transaction;
    try {
        transaction = database.transaction(request.stores, request.readWrite ? "readwrite" : "readonly");
    } catch (error) {
        throw failure(error, "The transaction could not start.");
    }

    const done = completion(transaction);
    const results = [];
    try {
        for (const operation of request.operations) {
            results.push(await run(transaction, operation));
        }
        await done;
        return JSON.stringify(results);
    } catch (error) {
        try {
            transaction.abort();
        } catch {
            // Already aborted by the failed request.
        }
        await done.catch(() => {});
        throw failure(error, "The transaction failed.");
    }
}

async function describe(name) {
    let database = connections.get(name);
    const borrowed = !database;
    if (borrowed) {
        database = await requestResult(indexedDB.open(name));
    }

    try {
        const encoder = new TextEncoder();
        const stores = [];
        let sizeBytes = 0;
        const names = Array.from(database.objectStoreNames).sort();
        if (names.length > 0) {
            const transaction = database.transaction(names, "readonly");
            for (const storeName of names) {
                const store = transaction.objectStore(storeName);
                const values = await requestResult(store.getAll());
                const keys = await requestResult(store.getAllKeys());
                for (let i = 0; i < values.length; i++) {
                    sizeBytes += encoder.encode(JSON.stringify(values[i]) ?? "").length + encoder.encode(JSON.stringify(keys[i]) ?? "").length;
                }
                stores.push({ name: storeName, recordCount: values.length });
            }
        }
        return { name, version: database.version, stores, sizeBytes };
    } finally {
        if (borrowed) {
            database.close();
        }
    }
}

/** Lists the site databases of this browser, with their stores and size (UTF-8 bytes of the records' JSON). */
export async function list(knownNamesJson) {
    const names = new Set();
    let enumerated = false;
    if (typeof indexedDB.databases === "function") {
        for (const info of await indexedDB.databases()) {
            if (info.name && info.name.startsWith(prefix)) {
                names.add(info.name);
            }
        }
        enumerated = true;
    }
    if (!enumerated) {
        // Without indexedDB.databases(), only the names the OS opened this session are known.
        for (const name of JSON.parse(knownNamesJson || "[]")) {
            names.add(name);
        }
    }

    const result = [];
    for (const name of Array.from(names).sort()) {
        result.push(await describe(name));
    }
    return JSON.stringify(result);
}

/** Deletes a site database, closing this page's connection first. */
export async function remove(name) {
    requireName(name);
    forget(name);
    await requestResult(indexedDB.deleteDatabase(name));
}
