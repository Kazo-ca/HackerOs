/**
 * Kazo OS host accessibility probe (ADR 0105). Reports the host's own accessibility settings as
 * raw media-query results only; what `auto` resolves to is decided in C#
 * (AccessibilitySettingsDocuments.Resolve), like platformEnvironmentProbe.js leaves its decision to C#.
 */

const QUERIES = {
    prefersMoreContrast: '(prefers-contrast: more)',
    forcedColors: '(forced-colors: active)',
    prefersReducedMotion: '(prefers-reduced-motion: reduce)'
};

function matches(query) {
    return !!(window.matchMedia && window.matchMedia(query).matches);
}

/** Reads every signal once. */
export function readSignals() {
    return {
        prefersMoreContrast: matches(QUERIES.prefersMoreContrast),
        forcedColors: matches(QUERIES.forcedColors),
        prefersReducedMotion: matches(QUERIES.prefersReducedMotion)
    };
}

/** Calls OnHostAccessibilityChanged on the .NET probe whenever one signal changes; returns a disposable. */
export function observeSignals(dotNetHelper) {
    const notify = () => dotNetHelper.invokeMethodAsync('OnHostAccessibilityChanged', readSignals()).catch(() => {});
    const lists = window.matchMedia
        ? Object.values(QUERIES).map(query => window.matchMedia(query)).filter(list => list.addEventListener)
        : [];
    lists.forEach(list => list.addEventListener('change', notify));
    return {
        dispose: () => lists.forEach(list => list.removeEventListener('change', notify))
    };
}
