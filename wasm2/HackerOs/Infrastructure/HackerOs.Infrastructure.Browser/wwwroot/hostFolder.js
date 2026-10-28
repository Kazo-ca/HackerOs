// Host folders of HackerOS (ADR 0181), the page half of Interop/BrowserHostFolderAccess.cs: folders of
// the real computer, chosen in the browser's folder dialog (File System Access API, Chrome and Edge on
// a computer) and mounted as network drives.
//
// - pick() opens showDirectoryPicker in read-write mode; the browser needs a recent user gesture;
// - the chosen FileSystemDirectoryHandle is kept in the IndexedDB database "hackeros-host-folders"
//   (a handle can be stored, but is not JSON), so load() finds it again after a reload; the browser
//   then asks for its permission again (queryPermission answers "prompt") unless the user chose
//   "Allow on every visit";
// - .NET holds a number per open folder; paths are arrays of names below the folder.
//
// Errors are thrown as Error("hostfolder:<Kind>:<message>"), Kind being a HostFolderErrorKind.
// Tests replace the dialog with globalThis.__hackerOsPickHostFolder, a function that resolves with
// a directory handle (an origin private folder of navigator.storage.getDirectory(), which has the same
// interface and needs no dialog).

const databaseName = 'hackeros-host-folders';
const storeName = 'handles';
const folders = new Map();
let nextId = 1;

function fail(kind, message) {
    return new Error(`hostfolder:${kind}:${message}`);
}

function translate(error) {
    if (error instanceof Error && error.message.startsWith('hostfolder:')) {
        return error;
    }

    const message = error && error.message ? error.message : String(error);
    switch (error && error.name) {
        case 'NotFoundError': return fail('NotFound', message);
        case 'TypeMismatchError': return fail('TypeMismatch', message);
        case 'InvalidModificationError': return fail('NotEmpty', message);
        case 'NotAllowedError':
        case 'SecurityError': return fail('NotAllowed', message);
        case 'QuotaExceededError': return fail('NoSpace', message);
        case 'TypeError': return fail('InvalidName', message);
        default: return fail('Failure', message);
    }
}

async function guard(action) {
    try {
        return await action();
    } catch (error) {
        throw translate(error);
    }
}

function openDatabase() {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open(databaseName, 1);
        request.onupgradeneeded = () => request.result.createObjectStore(storeName);
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
    });
}

async function storeRequest(mode, run) {
    const database = await openDatabase();
    try {
        return await new Promise((resolve, reject) => {
            const transaction = database.transaction(storeName, mode);
            const request = run(transaction.objectStore(storeName));
            transaction.oncomplete = () => resolve(request.result);
            transaction.onerror = () => reject(transaction.error);
            transaction.onabort = () => reject(transaction.error);
        });
    } finally {
        database.close();
    }
}

function register(handle) {
    const id = nextId++;
    folders.set(id, handle);
    return { id, name: handle.name };
}

function root(id) {
    const handle = folders.get(id);
    if (!handle) {
        throw fail('Failure', 'the folder is closed');
    }

    return handle;
}

async function directory(id, path) {
    let current = root(id);
    for (const name of path) {
        current = await current.getDirectoryHandle(name);
    }

    return current;
}

// The handle at path, file or folder, or a NotFound error.
async function entry(id, path) {
    if (path.length === 0) {
        return root(id);
    }

    const parent = await directory(id, path.slice(0, -1));
    const name = path[path.length - 1];
    try {
        return await parent.getFileHandle(name);
    } catch (error) {
        if (error && error.name === 'TypeMismatchError') {
            return await parent.getDirectoryHandle(name);
        }

        throw error;
    }
}

async function describe(handle) {
    if (handle.kind === 'directory') {
        return { name: handle.name, kind: 'directory', size: 0, lastModified: null };
    }

    const file = await handle.getFile();
    return { name: handle.name, kind: 'file', size: file.size, lastModified: file.lastModified };
}

export function isSupported() {
    return typeof globalThis.__hackerOsPickHostFolder === 'function'
        || typeof globalThis.showDirectoryPicker === 'function';
}

// Opens the folder dialog; resolves with { key, id, name }, or null when the user closed it.
export async function pick() {
    if (!isSupported()) {
        throw fail('Failure', 'notsupported');
    }

    if (navigator.userActivation && !navigator.userActivation.isActive && !globalThis.__hackerOsPickHostFolder) {
        throw fail('Failure', 'user activation required');
    }

    let handle;
    try {
        handle = globalThis.__hackerOsPickHostFolder
            ? await globalThis.__hackerOsPickHostFolder()
            : await globalThis.showDirectoryPicker({ id: 'hackeros-network-drive', mode: 'readwrite' });
    } catch (error) {
        if (error && error.name === 'AbortError') {
            return null;
        }

        throw translate(error);
    }

    if (!handle) {
        return null;
    }

    const key = crypto.randomUUID();
    await guard(() => storeRequest('readwrite', store => store.put(handle, key)));
    return { key, ...register(handle) };
}

// Finds a stored folder again: { id, name }, or null when the key is unknown.
export async function load(key) {
    const handle = await guard(() => storeRequest('readonly', store => store.get(key)));
    return handle ? register(handle) : null;
}

export async function forget(key) {
    await guard(() => storeRequest('readwrite', store => store.delete(key)));
}

export function release(id) {
    folders.delete(id);
}

async function permission(id, ask) {
    const handle = root(id);
    if (typeof handle.queryPermission !== 'function') {
        return 'granted';
    }

    const options = { mode: 'readwrite' };
    return ask ? await handle.requestPermission(options) : await handle.queryPermission(options);
}

export function queryPermission(id) {
    return guard(() => permission(id, false));
}

export function requestPermission(id) {
    return guard(() => permission(id, true));
}

export function stat(id, path) {
    return guard(async () => describe(await entry(id, path)));
}

export function list(id, path) {
    return guard(async () => {
        const folder = await directory(id, path);
        const entries = [];
        for await (const handle of folder.values()) {
            entries.push(await describe(handle));
        }

        return entries;
    });
}

export function read(id, path) {
    return guard(async () => {
        const handle = await entry(id, path);
        if (handle.kind !== 'file') {
            throw fail('TypeMismatch', 'not a file');
        }

        return new Uint8Array(await (await handle.getFile()).arrayBuffer());
    });
}

export function create(id, path, kind) {
    return guard(async () => {
        const parent = await directory(id, path.slice(0, -1));
        const name = path[path.length - 1];
        let exists = true;
        try {
            await entry(id, path);
        } catch (error) {
            if (!error || error.name !== 'NotFoundError') {
                throw error;
            }

            exists = false;
        }

        if (exists) {
            throw fail('AlreadyExists', `${name} exists`);
        }

        if (kind === 'directory') {
            await parent.getDirectoryHandle(name, { create: true });
        } else {
            await parent.getFileHandle(name, { create: true });
        }
    });
}

// Replaces the content of an existing file: the browser writes a temporary file and swaps it in on close.
export function write(id, path, bytes) {
    return guard(async () => {
        const parent = await directory(id, path.slice(0, -1));
        const handle = await parent.getFileHandle(path[path.length - 1]);
        const writable = await handle.createWritable({ keepExistingData: false });
        try {
            await writable.write(bytes);
            await writable.close();
        } catch (error) {
            await writable.abort().catch(() => { });
            throw error;
        }
    });
}

export function remove(id, path, recursive) {
    return guard(async () => {
        const parent = await directory(id, path.slice(0, -1));
        await parent.removeEntry(path[path.length - 1], { recursive });
    });
}

// Moves in one call when the browser has FileSystemHandle.move; false when it has not.
export function move(id, source, destination) {
    return guard(async () => {
        const handle = await entry(id, source);
        if (typeof handle.move !== 'function') {
            return false;
        }

        const parent = await directory(id, destination.slice(0, -1));
        try {
            await handle.move(parent, destination[destination.length - 1]);
        } catch (error) {
            // Some browsers have move() for the origin private folder only, or for files only.
            if (error && error.name === 'NotSupportedError') {
                return false;
            }

            throw error;
        }

        return true;
    });
}
