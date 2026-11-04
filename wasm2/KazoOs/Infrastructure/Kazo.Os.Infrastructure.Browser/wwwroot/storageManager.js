export async function getStorageEstimate() {
    ensureStorageManager();
    const [estimate, isPersisted] = await Promise.all([
        navigator.storage.estimate(),
        navigator.storage.persisted()
    ]);

    return {
        usageBytes: Math.trunc(estimate.usage ?? 0),
        quotaBytes: Math.trunc(estimate.quota ?? 0),
        isPersisted
    };
}

export async function requestPersistence() {
    ensureStorageManager();
    return navigator.storage.persist();
}

function ensureStorageManager() {
    if (!navigator.storage?.estimate || !navigator.storage?.persist || !navigator.storage?.persisted) {
        throw new DOMException("Browser storage management is unavailable.", "NotSupportedError");
    }
}