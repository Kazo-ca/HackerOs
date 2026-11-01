// HackerOS page bridge (ADR 0075 §3).
//
// Injected first in <head> of every page the Browser app writes into its isolated frame
// (sandbox="allow-scripts allow-forms", opaque origin, CSP connect-src 'none'). It is the page's
// only way out: fetch, XMLHttpRequest, links, forms, cookies, storage and dynamically added
// resources become messages to BrowserPageView.razor.js, which hands them to C#. The C# side
// sends them through the simulated network with HackHttpClient, so a site's JavaScript never
// reaches HackerOS nor the real Internet.
//
// Not a host script: it cannot be served from _content/ because the frame may load nothing but
// data: URLs, so HtmlPageComposer embeds it as text (see doc/en/wasm/dev/browser-html-rendering.md).
function (boot) {
    "use strict";

    const token = boot.token;
    const host = window.parent;
    const pageUrl = boot.url;
    const NativeRequest = window.Request;
    const NativeResponse = window.Response;
    const nativeSetAttribute = Element.prototype.setAttribute;
    const pending = new Map();
    let nextId = 1;
    let cookieString = boot.cookies || "";

    // ── Messaging ─────────────────────────────────────────────────────────

    function post(kind, type, payload, id) {
        host.postMessage({ hackBridge: kind, token, type, id, payload }, "*");
    }

    function emit(type, payload) {
        post("event", type, payload || {});
    }

    function request(type, payload) {
        return new Promise((resolve) => {
            const id = nextId++;
            pending.set(id, resolve);
            post("request", type, payload, id);
        });
    }

    // Registered before any page script, so the page's own "message" listeners never see replies.
    window.addEventListener("message", (event) => {
        const data = event.data;
        if (event.source !== host || !data || data.hackBridge !== "response" || data.token !== token) {
            return;
        }

        event.stopImmediatePropagation();
        const resolve = pending.get(data.id);
        if (resolve) {
            pending.delete(data.id);
            resolve(data.payload || {});
        }
    }, true);

    function toBase64(bytes) {
        let binary = "";
        for (let i = 0; i < bytes.length; i += 0x8000) {
            binary += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
        }
        return btoa(binary);
    }

    function fromBase64(text) {
        const binary = atob(text || "");
        const bytes = new Uint8Array(binary.length);
        for (let i = 0; i < binary.length; i++) {
            bytes[i] = binary.charCodeAt(i);
        }
        return bytes;
    }

    function define(target, name, getter) {
        try {
            Object.defineProperty(target, name, { configurable: true, get: getter });
        } catch {
            // A property the host browser does not let us redefine keeps its native behavior.
        }
    }

    // ── Console and errors (developer tools, ADR 0075 §2) ─────────────────

    function format(value) {
        if (typeof value === "string") {
            return value;
        }
        if (value instanceof Error) {
            return value.stack || String(value);
        }
        if (typeof value === "function" || typeof value === "symbol" || typeof value === "bigint") {
            return String(value);
        }
        try {
            const json = JSON.stringify(value);
            return json === undefined ? String(value) : json;
        } catch {
            return String(value);
        }
    }

    function log(level, message, source) {
        emit("console", { level, message: String(message), source: source || "" });
    }

    const consoleLevels = { debug: "debug", log: "log", info: "log", trace: "log", warn: "warning", error: "error" };
    for (const name of Object.keys(consoleLevels)) {
        const original = typeof console[name] === "function" ? console[name].bind(console) : null;
        console[name] = function (...args) {
            log(consoleLevels[name], args.map(format).join(" "));
            if (original) {
                original(...args);
            }
        };
    }

    const originalAssert = console.assert ? console.assert.bind(console) : null;
    console.assert = function (condition, ...args) {
        if (!condition) {
            log("error", "Assertion failed: " + (args.length ? args.map(format).join(" ") : "console.assert"));
        }
        if (originalAssert) {
            originalAssert(condition, ...args);
        }
    };

    function sourceOf(fileName, line) {
        if (!fileName || fileName.startsWith("data:") || fileName.startsWith("about:")) {
            return line ? "line " + line : "";
        }
        return line ? fileName + ":" + line : fileName;
    }

    window.addEventListener("error", (event) => {
        if (event.target && event.target !== window) {
            return;
        }
        const detail = event.error && event.error.stack ? event.error.stack : event.message;
        log("error", "Uncaught " + detail, sourceOf(event.filename, event.lineno));
    });

    window.addEventListener("unhandledrejection", (event) => {
        log("error", "Uncaught (in promise) " + format(event.reason));
    });

    // Resources the bridge loads itself are refused by the policy first (an <img> inserted with
    // innerHTML starts loading before the observer sees it): only unexpected refusals are shown.
    const handledUrls = new Set();
    document.addEventListener("securitypolicyviolation", (event) => {
        const blocked = event.blockedURI;
        setTimeout(() => {
            if (handledUrls.has(blocked)) {
                return;
            }
            log("error",
                "Refused to load '" + blocked + "' (" + event.effectiveDirective + "): HackerOS Browser pages reach the network through the browser only.",
                sourceOf(event.sourceFile, event.lineNumber));
        }, 50);
    });

    // ── Document identity ─────────────────────────────────────────────────
    // The frame's real URL is about:srcdoc; scripts reading document.URL see the page's address.
    // window.location cannot be redefined (it is unforgeable): it keeps about:srcdoc.

    define(document, "URL", () => pageUrl);
    define(document, "documentURI", () => pageUrl);
    define(document, "referrer", () => boot.referrer || "");

    // ── Cookies (ADR 0075 §3) ─────────────────────────────────────────────

    function applyCookie(current, assignment) {
        const parts = assignment.split(";");
        const pair = parts[0];
        const equals = pair.indexOf("=");
        const name = (equals < 0 ? "" : pair.slice(0, equals)).trim();
        const value = (equals < 0 ? pair : pair.slice(equals + 1)).trim();
        let expired = false;
        for (const attribute of parts.slice(1)) {
            const [key, raw] = attribute.split("=");
            const lower = (key || "").trim().toLowerCase();
            if (lower === "max-age" && Number(raw) <= 0) {
                expired = true;
            } else if (lower === "expires" && Date.parse(raw) <= Date.now()) {
                expired = true;
            }
        }

        const cookies = current ? current.split("; ").filter((entry) => entry.length > 0) : [];
        const kept = cookies.filter((entry) => {
            const at = entry.indexOf("=");
            return (at < 0 ? "" : entry.slice(0, at)) !== name;
        });
        if (!expired) {
            kept.push(name ? name + "=" + value : value);
        }
        return kept.join("; ");
    }

    try {
        Object.defineProperty(document, "cookie", {
            configurable: true,
            get: () => cookieString,
            set: (value) => {
                if (boot.isFile) {
                    return;
                }
                cookieString = applyCookie(cookieString, String(value));
                emit("cookie", { value: String(value) });
            },
        });
    } catch {
        // Keep the native (empty, opaque-origin) cookie jar.
    }

    // ── localStorage / sessionStorage (forbidden in an opaque origin, ADR 0075 §3) ──

    function makeStorage(area, initial) {
        const data = new Map(Object.entries(initial || {}));
        const quota = boot.storageQuota || 5 * 1024 * 1024;
        const size = () => {
            let total = 0;
            for (const [key, value] of data) {
                total += key.length + value.length;
            }
            return total;
        };

        const api = {
            get length() {
                return data.size;
            },
            key(index) {
                const keys = Array.from(data.keys());
                return index >= 0 && index < keys.length ? keys[index] : null;
            },
            getItem(key) {
                key = String(key);
                return data.has(key) ? data.get(key) : null;
            },
            setItem(key, value) {
                key = String(key);
                value = String(value);
                const previous = data.has(key) ? key.length + data.get(key).length : 0;
                if (size() - previous + key.length + value.length > quota) {
                    throw new DOMException("Failed to execute 'setItem' on 'Storage': Setting the value of '" + key + "' exceeded the quota.", "QuotaExceededError");
                }
                data.set(key, value);
                emit("storage", { area, op: "set", key, value });
            },
            removeItem(key) {
                key = String(key);
                if (data.delete(key)) {
                    emit("storage", { area, op: "remove", key });
                }
            },
            clear() {
                if (data.size > 0) {
                    data.clear();
                    emit("storage", { area, op: "clear" });
                }
            },
        };

        // Property access (localStorage.theme = "dark") behaves like the real Storage object.
        return new Proxy(api, {
            get(target, property) {
                if (typeof property === "symbol" || property in target) {
                    return Reflect.get(target, property);
                }
                return target.getItem(property);
            },
            set(target, property, value) {
                if (typeof property === "symbol" || property in target) {
                    return false;
                }
                target.setItem(property, value);
                return true;
            },
            deleteProperty(target, property) {
                target.removeItem(property);
                return true;
            },
            has(target, property) {
                return property in target || data.has(String(property));
            },
            ownKeys() {
                return Array.from(data.keys());
            },
            getOwnPropertyDescriptor(target, property) {
                const key = String(property);
                return data.has(key)
                    ? { value: data.get(key), enumerable: true, configurable: true, writable: true }
                    : undefined;
            },
        });
    }

    const localArea = makeStorage("local", boot.localStorage);
    const sessionArea = makeStorage("session", boot.sessionStorage);
    define(window, "localStorage", () => localArea);
    define(window, "sessionStorage", () => sessionArea);

    // ── fetch and XMLHttpRequest (ADR 0075 §3) ────────────────────────────

    async function send(input, init, kind) {
        // The native Request normalizes the URL (against <base>), headers and any body type.
        const req = new NativeRequest(input, init);
        const headers = [];
        req.headers.forEach((value, name) => headers.push([name, value]));
        let body = null;
        if (req.method !== "GET" && req.method !== "HEAD") {
            const bytes = new Uint8Array(await req.arrayBuffer());
            body = bytes.length > 0 ? toBase64(bytes) : null;
        }

        const reply = await request("fetch", {
            method: req.method,
            url: req.url,
            headers,
            body,
            kind,
            credentials: req.credentials,
            mode: req.mode,
        });
        if (typeof reply.cookies === "string") {
            cookieString = reply.cookies;
        }
        if (reply.error) {
            throw new TypeError(reply.message || "Failed to fetch");
        }
        return reply;
    }

    function abortError(signal) {
        return signal && signal.reason !== undefined
            ? signal.reason
            : new DOMException("The user aborted a request.", "AbortError");
    }

    function toResponse(reply) {
        const nullBody = reply.opaque || [101, 103, 204, 205, 304].includes(reply.status);
        const status = reply.opaque ? 200 : Math.min(599, Math.max(200, reply.status));
        const response = new NativeResponse(nullBody ? null : fromBase64(reply.body), {
            status,
            statusText: reply.opaque ? "" : reply.statusText || "",
            headers: reply.opaque ? [] : reply.headers || [],
        });
        define(response, "url", () => (reply.opaque ? "" : reply.url));
        define(response, "redirected", () => !!reply.redirected);
        define(response, "type", () => (reply.opaque ? "opaque" : reply.type || "basic"));
        if (reply.opaque) {
            define(response, "status", () => 0);
            define(response, "ok", () => false);
        }
        return response;
    }

    window.fetch = function fetch(input, init) {
        const signal = (init && init.signal) || (input instanceof NativeRequest ? input.signal : null);
        if (signal && signal.aborted) {
            return Promise.reject(abortError(signal));
        }

        const work = send(input, init, "fetch").then(toResponse);
        if (!signal) {
            return work;
        }

        return new Promise((resolve, reject) => {
            signal.addEventListener("abort", () => reject(abortError(signal)), { once: true });
            work.then(resolve, reject);
        });
    };

    const progressEvents = new Set(["loadstart", "progress", "load", "loadend", "error", "abort", "timeout"]);

    class BridgeXMLHttpRequest extends EventTarget {
        constructor() {
            super();
            this.readyState = 0;
            this.status = 0;
            this.statusText = "";
            this.responseType = "";
            this.response = "";
            this.responseText = "";
            this.responseXML = null;
            this.responseURL = "";
            this.timeout = 0;
            this.withCredentials = false;
            this.upload = new EventTarget();
            this._headers = [];
            this._responseHeaders = [];
            this._generation = 0;
        }

        open(method, url, async) {
            if (async === false) {
                throw new DOMException("Synchronous XMLHttpRequest is not supported by HackerOS Browser.", "InvalidAccessError");
            }
            this._generation++;
            this._method = String(method).toUpperCase();
            this._url = new URL(url, document.baseURI).href;
            this._headers = [];
            this._responseHeaders = [];
            this._sent = false;
            this.status = 0;
            this.statusText = "";
            this.response = this.responseType === "" || this.responseType === "text" ? "" : null;
            this.responseText = "";
            this.responseURL = "";
            this._state(1);
        }

        setRequestHeader(name, value) {
            if (this.readyState !== 1 || this._sent) {
                throw new DOMException("The object's state must be OPENED.", "InvalidStateError");
            }
            this._headers.push([String(name), String(value)]);
        }

        overrideMimeType(mimeType) {
            this._mimeType = String(mimeType);
        }

        getResponseHeader(name) {
            const lower = String(name).toLowerCase();
            const values = this._responseHeaders.filter((header) => header[0].toLowerCase() === lower).map((header) => header[1]);
            return values.length > 0 ? values.join(", ") : null;
        }

        getAllResponseHeaders() {
            return this._responseHeaders.map((header) => header[0].toLowerCase() + ": " + header[1] + "\r\n").join("");
        }

        abort() {
            const active = this._sent && this.readyState !== 4;
            this._generation++;
            if (active) {
                this._state(4);
                this._fire("abort");
                this._fire("loadend");
            }
            this.readyState = 0;
            this._sent = false;
        }

        send(body) {
            if (this.readyState !== 1 || this._sent) {
                throw new DOMException("The object's state must be OPENED.", "InvalidStateError");
            }
            this._sent = true;
            const generation = this._generation;
            const init = { method: this._method, headers: this._headers, credentials: this.withCredentials ? "include" : "same-origin" };
            if (body !== undefined && body !== null && this._method !== "GET" && this._method !== "HEAD") {
                init.body = body;
            }

            this._fire("loadstart");
            let timer = null;
            if (this.timeout > 0) {
                timer = setTimeout(() => {
                    if (generation !== this._generation) {
                        return;
                    }
                    this._generation++;
                    this._state(4);
                    this._fire("timeout");
                    this._fire("loadend");
                }, this.timeout);
            }

            send(this._url, init, "xhr").then((reply) => {
                if (generation !== this._generation) {
                    return;
                }
                clearTimeout(timer);
                this.status = reply.opaque ? 0 : reply.status;
                this.statusText = reply.statusText || "";
                this.responseURL = reply.url || "";
                this._responseHeaders = reply.headers || [];
                this._state(2);
                this._state(3);

                const bytes = fromBase64(reply.body);
                const text = new TextDecoder().decode(bytes);
                const type = this._mimeType || this.getResponseHeader("content-type") || "";
                switch (this.responseType) {
                    case "json":
                        try { this.response = JSON.parse(text); } catch { this.response = null; }
                        break;
                    case "arraybuffer":
                        this.response = bytes.buffer;
                        break;
                    case "blob":
                        this.response = new Blob([bytes], { type });
                        break;
                    case "document":
                        this.response = new DOMParser().parseFromString(text, type.includes("xml") ? "application/xml" : "text/html");
                        this.responseXML = this.response;
                        break;
                    default:
                        this.response = text;
                        this.responseText = text;
                }
                this._fire("progress");
                this._state(4);
                this._fire("load");
                this._fire("loadend");
            }, () => {
                if (generation !== this._generation) {
                    return;
                }
                clearTimeout(timer);
                this.status = 0;
                this._state(4);
                this._fire("error");
                this._fire("loadend");
            });
        }

        _state(value) {
            this.readyState = value;
            this._fire("readystatechange");
        }

        _fire(type) {
            const event = progressEvents.has(type) ? new ProgressEvent(type) : new Event(type);
            this.dispatchEvent(event);
            const handler = this["on" + type];
            if (typeof handler === "function") {
                handler.call(this, event);
            }
        }
    }

    for (const [name, value] of Object.entries({ UNSENT: 0, OPENED: 1, HEADERS_RECEIVED: 2, LOADING: 3, DONE: 4 })) {
        BridgeXMLHttpRequest[name] = value;
        BridgeXMLHttpRequest.prototype[name] = value;
    }
    window.XMLHttpRequest = BridgeXMLHttpRequest;

    if (navigator.sendBeacon) {
        navigator.sendBeacon = function sendBeacon(url, data) {
            send(url, { method: "POST", body: data, credentials: "include" }, "fetch").catch(() => {});
            return true;
        };
    }

    // ── Navigation: links, forms, history (ADR 0075 §3) ───────────────────
    // The frame never navigates itself: the browser loads the target and writes a new document.

    function withoutHash(url) {
        const at = url.indexOf("#");
        return at < 0 ? url : url.slice(0, at);
    }

    function scrollToFragment(hash) {
        const id = decodeURIComponent(hash.replace(/^#/, ""));
        if (!id) {
            window.scrollTo(0, 0);
            return;
        }
        const target = document.getElementById(id) || document.getElementsByName(id)[0];
        if (target) {
            target.scrollIntoView();
        }
    }

    // Window, bubbling: runs after the page's own handlers, so a page that calls preventDefault keeps control.
    window.addEventListener("click", (event) => {
        if (event.defaultPrevented || event.button !== 0) {
            return;
        }
        const anchor = event.target instanceof Element ? event.target.closest("a[href], area[href]") : null;
        if (!anchor) {
            return;
        }
        const href = anchor.href;
        if (!href || href.toLowerCase().startsWith("javascript:")) {
            return;
        }

        event.preventDefault();
        if (anchor.hasAttribute("download")) {
            emit("navigate", { url: href, method: "GET", download: true });
            return;
        }
        if (withoutHash(href) === withoutHash(pageUrl) && href.includes("#")) {
            scrollToFragment(new URL(href).hash);
            emit("fragment", { url: href });
            return;
        }
        emit("navigate", { url: href, method: "GET", target: anchor.target || "" });
    });

    function formEntries(form, submitter) {
        let data;
        try {
            data = new FormData(form, submitter || undefined);
        } catch {
            data = new FormData(form);
            if (submitter && submitter.name) {
                data.append(submitter.name, submitter.value);
            }
        }
        return data;
    }

    // ── Form history (ADR 0088 §6) ────────────────────────────────────────
    // Text-like fields with a name are remembered when their form is submitted, and offered back in a
    // <datalist> when a field of the same name gets the focus. Passwords, hidden fields and
    // autocomplete="off" (field or form) are never sent to the browser.

    const rememberedTypes = new Set(["text", "search", "email", "tel", "url", "number"]);
    const suggestionLists = new Map();
    let nextListId = 1;

    function rememberable(element) {
        if (!(element instanceof HTMLInputElement) || !rememberedTypes.has(element.type) || !element.name) {
            return false;
        }
        const off = (node) => node && (node.getAttribute("autocomplete") || "").trim().toLowerCase() === "off";
        return !off(element) && !off(element.form);
    }

    function rememberFields(form) {
        const fields = [];
        for (const element of Array.from(form.elements)) {
            if (rememberable(element) && !element.disabled && element.value.trim() !== "") {
                fields.push([element.name, element.value]);
            }
        }
        if (fields.length > 0) {
            emit("formhistory", { fields });
        }
    }

    document.addEventListener("focusin", async (event) => {
        const input = event.target;
        if (!rememberable(input)) {
            return;
        }
        const own = input.getAttribute("list");
        if (own && !own.startsWith("hackos-form-history-")) {
            return; // The page has its own suggestions.
        }

        let list = suggestionLists.get(input.name);
        if (!list) {
            list = document.createElement("datalist");
            list.id = "hackos-form-history-" + nextListId++;
            (document.body || document.documentElement).appendChild(list);
            suggestionLists.set(input.name, list);
            const reply = await request("formhistory", { name: input.name });
            for (const value of reply.values || []) {
                const option = document.createElement("option");
                option.value = value;
                list.appendChild(option);
            }
        }
        nativeSetAttribute.call(input, "list", list.id);
    }, true);

    async function submitForm(form, submitter) {
        const method = ((submitter && submitter.getAttribute("formmethod")) || form.getAttribute("method") || "get").toLowerCase();
        if (method === "dialog") {
            const dialog = form.closest("dialog");
            if (dialog) {
                dialog.close(submitter ? submitter.value : undefined);
            }
            return;
        }

        rememberFields(form);

        const actionAttribute = (submitter && submitter.getAttribute("formaction")) || form.getAttribute("action");
        const action = actionAttribute ? new URL(actionAttribute, document.baseURI).href : pageUrl;
        const enctype = ((submitter && submitter.getAttribute("formenctype")) || form.getAttribute("enctype") || "application/x-www-form-urlencoded").toLowerCase();
        const data = formEntries(form, submitter);
        const pairs = [];
        for (const [name, value] of data) {
            pairs.push([name, typeof value === "string" ? value : value.name]);
        }

        if (method !== "post") {
            const target = new URL(action);
            target.search = new URLSearchParams(pairs).toString();
            emit("navigate", { url: target.href, method: "GET" });
            return;
        }

        let body;
        let contentType;
        if (enctype === "multipart/form-data") {
            const encoded = new NativeRequest("https://form.invalid/", { method: "POST", body: data });
            body = new Uint8Array(await encoded.arrayBuffer());
            contentType = encoded.headers.get("content-type");
        } else if (enctype === "text/plain") {
            body = new TextEncoder().encode(pairs.map((pair) => pair[0] + "=" + pair[1] + "\r\n").join(""));
            contentType = "text/plain;charset=UTF-8";
        } else {
            body = new TextEncoder().encode(new URLSearchParams(pairs).toString());
            contentType = "application/x-www-form-urlencoded";
        }
        emit("navigate", { url: action, method: "POST", body: toBase64(body), contentType });
    }

    window.addEventListener("submit", (event) => {
        if (event.defaultPrevented) {
            return;
        }
        event.preventDefault();
        submitForm(event.target, event.submitter).catch((error) => log("error", "Form submission failed: " + format(error)));
    });

    // form.submit() fires no "submit" event and skips validation, like the native method.
    HTMLFormElement.prototype.submit = function submit() {
        submitForm(this, null).catch((error) => log("error", "Form submission failed: " + format(error)));
    };

    // pushState/replaceState to the page's URL space would throw in about:srcdoc: keep the state,
    // and tell the browser so its address bar and history follow the single-page app.
    for (const name of ["pushState", "replaceState"]) {
        const original = history[name].bind(history);
        history[name] = function (state, title, url) {
            original(state, title);
            if (url !== undefined && url !== null) {
                emit("history", { url: new URL(String(url), document.baseURI).href, replace: name === "replaceState" });
            }
        };
    }

    if (boot.refresh) {
        setTimeout(() => emit("navigate", { url: boot.refresh.url || pageUrl, method: "GET", replace: true }), boot.refresh.seconds * 1000);
    }

    // ── Refused APIs (questionnaire 12) ───────────────────────────────────

    function refuse(name) {
        return function () {
            const message = name + " is not supported by HackerOS Browser yet (ADR 0075).";
            log("error", message);
            throw new DOMException(message, "NotSupportedError");
        };
    }

    window.WebSocket = refuse("WebSocket");
    window.EventSource = refuse("EventSource");
    window.open = function () {
        log("error", "window.open() is blocked by HackerOS Browser (ADR 0075).");
        return null;
    };
    if (navigator.geolocation) {
        const deny = (success, error) => {
            log("warning", "Geolocation is not available in HackerOS Browser.");
            if (typeof error === "function") {
                error({ code: 1, message: "User denied Geolocation", PERMISSION_DENIED: 1, POSITION_UNAVAILABLE: 2, TIMEOUT: 3 });
            }
            return 0;
        };
        navigator.geolocation.getCurrentPosition = deny;
        navigator.geolocation.watchPosition = deny;
    }

    // Modal dialogs would freeze HackerOS with the page: alert() shows a banner, the others answer "no".
    window.alert = function (message) {
        emit("dialog", { kind: "alert", message: message === undefined ? "" : String(message) });
    };
    window.confirm = function (message) {
        log("warning", "confirm() is not supported; answered false: " + String(message));
        return false;
    };
    window.prompt = function (message) {
        log("warning", "prompt() is not supported; answered null: " + String(message));
        return null;
    };
    window.print = function () {};

    // ── Browser shortcuts typed while the page has the focus ──────────────

    window.addEventListener("keydown", (event) => {
        const key = event.key;
        const lower = typeof key === "string" ? key.toLowerCase() : "";
        const handled = key === "F5" || key === "F12"
            || (event.ctrlKey && event.shiftKey && lower === "i")
            || (event.ctrlKey && !event.shiftKey && lower === "u")
            || (event.ctrlKey && !event.shiftKey && lower === "r")
            || (event.altKey && (key === "ArrowLeft" || key === "ArrowRight"))
            || key === "BrowserBack" || key === "BrowserForward"
            || (event.ctrlKey && event.shiftKey && key === "Delete");
        if (handled) {
            event.preventDefault();
            emit("key", { key, ctrl: event.ctrlKey, shift: event.shiftKey, alt: event.altKey });
        }
    }, true);

    // The mouse's Back (button 4) and Forward (button 5) buttons pressed over the page (ADR 0220 §4): the
    // browser window goes back or forward in its own history instead of the host tab. They travel as the
    // Browser Back / Forward keys.
    const isNavigationButton = (event) => event.button === 3 || event.button === 4;
    for (const type of ["mousedown", "auxclick"]) {
        window.addEventListener(type, (event) => {
            if (isNavigationButton(event)) {
                event.preventDefault();
            }
        }, true);
    }

    window.addEventListener("mouseup", (event) => {
        if (!isNavigationButton(event)) {
            return;
        }

        event.preventDefault();
        emit("key", { key: event.button === 3 ? "BrowserBack" : "BrowserForward", ctrl: false, shift: false, alt: false });
    }, true);

    // ── Resources added after the load (ADR 0075 §3) ──────────────────────

    function needsLoad(value) {
        return typeof value === "string" && value.trim().length > 0 && !/^\s*(data:|blob:|about:|javascript:|#)/i.test(value);
    }

    function kindOf(element, attribute) {
        switch (element.localName) {
            case "script": return "script";
            case "link": return "stylesheet";
            case "video":
            case "audio":
            case "track": return attribute === "poster" ? "image" : "media";
            case "source": return element.parentElement && element.parentElement.localName === "picture" ? "image" : "media";
            default: return "image";
        }
    }

    function loadInto(element, attribute, value) {
        let url;
        try {
            url = new URL(value, document.baseURI).href;
        } catch {
            return;
        }
        handledUrls.add(url);
        nativeSetAttribute.call(element, "data-hack-" + attribute, url);
        request("resource", { url, kind: kindOf(element, attribute) }).then((reply) => {
            if (element.getAttribute("data-hack-" + attribute) === url) {
                nativeSetAttribute.call(element, attribute, reply.dataUrl || "data:,");
            }
        });
    }

    function watched(element, attribute) {
        switch (attribute) {
            case "src": return ["img", "script", "video", "audio", "source", "track", "input"].includes(element.localName);
            case "href": return (element.localName === "link" && /(^|\s)stylesheet(\s|$)/i.test(element.rel)) || element.localName === "image";
            case "poster": return element.localName === "video";
            default: return false;
        }
    }

    // Setting src/href from script: the request goes through the bridge before the element gets a
    // data: URL. A <script> connected without src does not start, so it runs once the URL is set.
    function interceptProperty(prototype, property) {
        const descriptor = Object.getOwnPropertyDescriptor(prototype, property);
        if (!descriptor || !descriptor.set) {
            return;
        }
        Object.defineProperty(prototype, property, {
            configurable: true,
            enumerable: descriptor.enumerable,
            get: descriptor.get,
            set(value) {
                if (watched(this, property) && needsLoad(String(value))) {
                    loadInto(this, property, String(value));
                    return;
                }
                descriptor.set.call(this, value);
            },
        });
    }

    interceptProperty(HTMLImageElement.prototype, "src");
    interceptProperty(HTMLScriptElement.prototype, "src");
    interceptProperty(HTMLMediaElement.prototype, "src");
    interceptProperty(HTMLSourceElement.prototype, "src");
    interceptProperty(HTMLVideoElement.prototype, "poster");
    interceptProperty(HTMLLinkElement.prototype, "href");

    Element.prototype.setAttribute = function setAttribute(name, value) {
        const attribute = String(name).toLowerCase();
        if (watched(this, attribute) && needsLoad(String(value))) {
            loadInto(this, attribute, String(value));
            return;
        }
        return nativeSetAttribute.call(this, name, value);
    };

    // Markup added with innerHTML/insertAdjacentHTML: the observer catches it after insertion.
    function scan(element) {
        for (const attribute of ["src", "href", "poster"]) {
            if (element.hasAttribute && element.hasAttribute(attribute) && watched(element, attribute)
                && needsLoad(element.getAttribute(attribute))) {
                loadInto(element, attribute, element.getAttribute(attribute));
            }
        }
    }

    new MutationObserver((records) => {
        for (const record of records) {
            for (const node of record.addedNodes) {
                if (node.nodeType !== 1) {
                    continue;
                }
                scan(node);
                for (const child of node.querySelectorAll("[src], [href], [poster]")) {
                    scan(child);
                }
            }
        }
    }).observe(document, { childList: true, subtree: true });

    // ── Title ─────────────────────────────────────────────────────────────

    let lastTitle = null;
    function reportTitle() {
        if (document.title !== lastTitle) {
            lastTitle = document.title;
            emit("title", { title: lastTitle });
        }
    }
    new MutationObserver(reportTitle).observe(document, { subtree: true, childList: true, characterData: true });
    document.addEventListener("DOMContentLoaded", reportTitle);

    // Last: tells the browser this document is its own (not the frame escaping to another URL).
    post("event", "ready", { url: pageUrl });
    document.currentScript && document.currentScript.remove();
}
