const connections = new Map();

function interopError(error, fallbackMessage) {
    if (error instanceof DOMException) {
        return new Error(`${error.name}: ${error.message}`);
    }
    return error instanceof Error ? error : new Error(fallbackMessage);
}

function requestResult(request) {
    return new Promise((resolve, reject) => {
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(interopError(request.error, "IndexedDB request failed."));
    });
}

function transactionCompletion(transaction) {
    return new Promise((resolve, reject) => {
        transaction.oncomplete = () => resolve();
        transaction.onabort = () => reject(interopError(
            transaction.error,
            "IndexedDB transaction was aborted."));
        transaction.onerror = () => reject(interopError(
            transaction.error,
            "IndexedDB transaction failed."));
    });
}

function normalizeKeyPath(keyPath) {
    if (!Array.isArray(keyPath)) {
        return keyPath;
    }

    if (keyPath.length === 0) {
        return undefined;
    }

    return keyPath.length === 1 ? keyPath[0] : keyPath;
}

function recordsEqual(left, right) {
    if (Object.is(left, right)) {
        return true;
    }
    if (left === null || right === null || typeof left !== "object" || typeof right !== "object") {
        return false;
    }
    if (Array.isArray(left) !== Array.isArray(right)) {
        return false;
    }
    const leftKeys = Object.keys(left).sort();
    const rightKeys = Object.keys(right).sort();
    return leftKeys.length === rightKeys.length &&
        leftKeys.every((key, index) => key === rightKeys[index] && recordsEqual(left[key], right[key]));
}

function applyMigrationStep(database, transaction, migrationStep) {
    for (const storeName of migrationStep.deleteObjectStores ?? []) {
        if (database.objectStoreNames.contains(storeName)) {
            database.deleteObjectStore(storeName);
        }
    }

    for (const definition of migrationStep.createObjectStores ?? []) {
        if (database.objectStoreNames.contains(definition.name)) {
            continue;
        }

        const keyPath = normalizeKeyPath(definition.keyPath);
        const options = { autoIncrement: definition.autoIncrement === true };
        if (keyPath !== undefined) {
            options.keyPath = keyPath;
        }

        const store = database.createObjectStore(definition.name, options);
        for (const index of definition.indexes ?? []) {
            store.createIndex(index.name, normalizeKeyPath(index.keyPath), {
                unique: index.unique === true,
                multiEntry: index.multiEntry === true
            });
        }
    }

    for (const change of migrationStep.deleteIndexes ?? []) {
        const store = transaction.objectStore(change.objectStoreName);
        if (store.indexNames.contains(change.indexName)) {
            store.deleteIndex(change.indexName);
        }
    }

    for (const change of migrationStep.createIndexes ?? []) {
        const store = transaction.objectStore(change.objectStoreName);
        if (!store.indexNames.contains(change.index.name)) {
            store.createIndex(change.index.name, normalizeKeyPath(change.index.keyPath), {
                unique: change.index.unique === true,
                multiEntry: change.index.multiEntry === true
            });
        }
    }
}

function applyUpgrade(database, transaction, migrationPlan, oldVersion, newVersion) {
    const requiredSteps = (migrationPlan.steps ?? []).filter(step =>
        step.targetVersion > oldVersion && step.targetVersion <= newVersion);
    const expectedTargets = Array.from(
        { length: newVersion - oldVersion },
        (_, index) => oldVersion + index + 1);

    if (requiredSteps.length !== expectedTargets.length ||
        requiredSteps.some((step, index) => step.targetVersion !== expectedTargets[index])) {
        transaction.abort();
        throw new Error(`No contiguous IndexedDB migration path exists from version ${oldVersion} to ${newVersion}.`);
    }

    for (const step of requiredSteps) {
        applyMigrationStep(database, transaction, step);
    }
}

/** Opens the named database and applies the C#-supplied declarative upgrade plan when required. */
export async function openDatabase(databaseName, version, migrationPlan = {}) {
    if (!databaseName || !Number.isInteger(version) || version < 1) {
        throw new Error("A database name and positive integer version are required.");
    }

    const cacheKey = `${databaseName}:${version}`;
    if (connections.has(cacheKey)) {
        return;
    }

    for (const [key, database] of connections) {
        if (database.name === databaseName) {
            database.close();
            connections.delete(key);
        }
    }

    const request = indexedDB.open(databaseName, version);
    request.onupgradeneeded = event =>
        applyUpgrade(request.result, request.transaction, migrationPlan, event.oldVersion, event.newVersion);
    request.onblocked = () => request.transaction?.abort();

    const database = await requestResult(request);
    database.onversionchange = () => {
        database.close();
        connections.delete(cacheKey);
    };
    connections.set(cacheKey, database);
}

async function executeOperation(transaction, operation) {
    const store = transaction.objectStore(operation.objectStoreName);
    const source = operation.indexName ? store.index(operation.indexName) : store;

    switch (operation.kind) {
        case "get":
            return requestResult(source.get(operation.key));
        case "getAll":
            return requestResult(operation.query === undefined
                ? source.getAll()
                : source.getAll(operation.query, operation.count));
        case "getAllKeys":
            return requestResult(operation.query === undefined
                ? source.getAllKeys()
                : source.getAllKeys(operation.query, operation.count));
        case "count":
            return requestResult(operation.query === undefined
                ? source.count()
                : source.count(operation.query));
        case "put":
            return requestResult(store.keyPath !== null || operation.key == null
                ? store.put(operation.value)
                : store.put(operation.value, operation.key));
        case "compareAndPut": {
            if (!operation.compareProperty) {
                throw new Error("compareAndPut requires compareProperty.");
            }

            const current = await requestResult(store.get(operation.key));
            const actualValue = current?.[operation.compareProperty];
            if (actualValue !== operation.expectedValue) {
                return { committed: false, actualValue: actualValue ?? null };
            }

            await requestResult(store.keyPath !== null || operation.key == null
                ? store.put(operation.value)
                : store.put(operation.value, operation.key));
            return { committed: true, actualValue };
        }
        case "assertPropertyEquals": {
            if (!operation.compareProperty) {
                throw new Error("assertPropertyEquals requires compareProperty.");
            }

            const current = await requestResult(store.get(operation.key));
            const actualValue = current?.[operation.compareProperty];
            if (actualValue !== operation.expectedValue) {
                throw new DOMException(
                    `${operation.failureCode ?? "indexeddb.precondition-failed"}: '${operation.compareProperty}'.`,
                    "ConstraintError");
            }

            return actualValue;
        }
        case "add":
            return requestResult(store.keyPath !== null || operation.key == null
                ? store.add(operation.value)
                : store.add(operation.value, operation.key));
        case "addIfAbsent": {
            const current = await requestResult(store.get(operation.key));
            if (current !== undefined) {
                return { added: false };
            }

            await requestResult(store.keyPath !== null || operation.key === undefined
                ? store.add(operation.value)
                : store.add(operation.value, operation.key));
            return { added: true };
        }
        case "addIfAbsentOrEqual": {
            const recordKey = Array.isArray(store.keyPath)
                ? store.keyPath.map(property => operation.value[property])
                : operation.value[store.keyPath];
            const current = await requestResult(store.get(recordKey));
            if (current !== undefined) {
                if (!recordsEqual(current, operation.value)) {
                    throw new DOMException(
                        `${operation.failureCode ?? "indexeddb.record-conflict"}.`,
                        "ConstraintError");
                }
                return { added: false };
            }

            await requestResult(store.add(operation.value));
            return { added: true };
        }
        case "delete":
            return requestResult(store.delete(operation.key));
        case "trimOldest": {
            if (!operation.indexName || !Number.isInteger(operation.count) || operation.count < 1) {
                throw new Error("trimOldest requires an index and a positive retained count.");
            }

            const total = await requestResult(store.count());
            let remaining = Math.max(0, total - operation.count);
            if (remaining === 0) {
                return { deleted: 0 };
            }

            const cursorRequest = store.index(operation.indexName).openKeyCursor();
            const deleted = await new Promise((resolve, reject) => {
                let deletedCount = 0;
                cursorRequest.onerror = () => reject(cursorRequest.error);
                cursorRequest.onsuccess = () => {
                    const cursor = cursorRequest.result;
                    if (!cursor || remaining === 0) {
                        resolve(deletedCount);
                        return;
                    }

                    const deleteRequest = store.delete(cursor.primaryKey);
                    deleteRequest.onerror = () => reject(deleteRequest.error);
                    deleteRequest.onsuccess = () => {
                        deletedCount++;
                        remaining--;
                        cursor.continue();
                    };
                };
            });
            return { deleted };
        }
        case "deleteAllByIndexIfUnreferenced": {
            if (!operation.indexName || !operation.referenceObjectStoreName ||
                !operation.referenceIndexName) {
                throw new Error("deleteAllByIndexIfUnreferenced requires source and reference indexes.");
            }

            const referenceStore = transaction.objectStore(operation.referenceObjectStoreName);
            const referenceCount = await requestResult(
                referenceStore.index(operation.referenceIndexName).count(operation.query));
            if (referenceCount !== 0) {
                return { deleted: 0, referenced: true };
            }

            const keys = await requestResult(store.index(operation.indexName).getAllKeys(operation.query));
            for (const key of keys) {
                await requestResult(store.delete(key));
            }
            return { deleted: keys.length, referenced: false };
        }
        case "resolvePath": {
            // Path lookup (namei) in one transaction: start at the entry `key`, then for each
            // name in `query` read the (parentId, name) link and the entry it points to. Done in
            // C# it cost two round trips per component; here the whole walk is one request batch
            // and one consistent snapshot. Returns the entries found, one per name, stopping
            // before the first missing name. A link that points to no entry (corruption) ends
            // the list with { danglingLink, entryId }, reported by the caller.
            if (!operation.referenceObjectStoreName || !Array.isArray(operation.query)) {
                throw new Error("resolvePath requires a link store and an array of names.");
            }

            const links = transaction.objectStore(operation.referenceObjectStoreName);
            const chain = [];
            let current = await requestResult(store.get(operation.key));
            for (const name of operation.query) {
                if (current === undefined) {
                    break;
                }

                const link = await requestResult(links.get([current.id, name]));
                if (link === undefined) {
                    break;
                }

                current = await requestResult(store.get(link.entryId));
                chain.push(current ?? { danglingLink: name, entryId: link.entryId });
            }

            return chain;
        }
        case "clear":
            return requestResult(store.clear());
        default:
            throw new Error(`Unsupported IndexedDB operation kind '${operation.kind}'.`);
    }
}

/** Executes all operations in one IndexedDB transaction and returns positional results. */
export async function executeTransaction(databaseName, version, objectStoreNames, mode, operations) {
    const database = connections.get(`${databaseName}:${version}`);
    if (!database) {
        throw new Error(`IndexedDB database '${databaseName}' version ${version} is not open.`);
    }
    if (mode !== "readonly" && mode !== "readwrite") {
        throw new Error("Transaction mode must be 'readonly' or 'readwrite'.");
    }
    if (!Array.isArray(objectStoreNames) || objectStoreNames.length === 0) {
        throw new Error("At least one object store is required.");
    }
    if (!Array.isArray(operations)) {
        throw new Error("Operations must be an array.");
    }

    const transaction = database.transaction(objectStoreNames, mode);
    const completion = transactionCompletion(transaction);
    const results = [];

    try {
        for (const operation of operations) {
            if (!objectStoreNames.includes(operation.objectStoreName)) {
                throw new Error(`Operation store '${operation.objectStoreName}' is outside the transaction boundary.`);
            }
            results.push(await executeOperation(transaction, operation));
        }
        await completion;
        return results;
    } catch (error) {
        try {
            transaction.abort();
        } catch (abortError) {
            if (abortError.name !== "InvalidStateError") {
                throw abortError;
            }
        }
        await completion.catch(() => {});
        throw error;
    }
}

/** Closes cached connections and deletes a database. Call only from explicit recovery/test flows. */
export async function deleteDatabase(databaseName) {
    for (const [key, database] of connections) {
        if (database.name === databaseName) {
            database.close();
            connections.delete(key);
        }
    }

    await requestResult(indexedDB.deleteDatabase(databaseName));
}
