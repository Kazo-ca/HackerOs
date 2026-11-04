// Host side of the Browser's isolated page frame (ADR 0075 §1 and §3).
//
// Only what C# cannot reach lives here: creating the sandboxed <iframe> (opaque origin, no
// allow-same-origin), writing its srcdoc, and relaying postMessage traffic between the page
// bridge (Bridge/PageBridge.js, injected in the page) and BrowserPageView's [JSInvokable] methods.
// Every decision (network, CORS, cookies, storage) is made in C#.

class PageFrame {
    constructor(host, dotnet) {
        this.host = host;
        this.dotnet = dotnet;
        this.frame = null;
        this.token = null;
        this.loads = 0;
        this.readies = 0;
        this.onMessage = (event) => this.receive(event);
        window.addEventListener("message", this.onMessage);
    }

    // Writes a new document. A fresh iframe each time: nothing of the previous page survives.
    show(html, token) {
        this.clear();
        this.token = token;
        this.loads = 0;
        this.readies = 0;

        const frame = document.createElement("iframe");
        frame.className = "page-frame";
        frame.setAttribute("sandbox", "allow-scripts allow-forms");
        frame.setAttribute("referrerpolicy", "no-referrer");
        frame.setAttribute("title", "Page content");
        frame.addEventListener("load", () => this.loaded(frame));
        frame.srcdoc = html;
        this.frame = frame;
        this.host.appendChild(frame);
    }

    clear() {
        if (this.frame) {
            this.frame.remove();
            this.frame = null;
        }
        this.token = null;
    }

    focus() {
        if (this.frame) {
            this.frame.focus();
        }
    }

    // Every document the browser writes announces itself ("ready") as its bridge starts. A load
    // with no announcement means the page navigated the frame by script (location.href = …),
    // which the bridge cannot intercept: the browser replaces it rather than show an unbridged
    // document. The announcement is a posted message, which may be delivered after the load
    // event: the count is compared a moment later.
    loaded(frame) {
        if (frame !== this.frame) {
            return;
        }
        const load = ++this.loads;
        setTimeout(() => {
            if (frame === this.frame && this.readies < load) {
                this.dotnet.invokeMethodAsync("OnFrameEscaped");
                this.clear();
            }
        }, 500);
    }

    receive(event) {
        const data = event.data;
        if (!this.frame || event.source !== this.frame.contentWindow || !data || data.token !== this.token) {
            return;
        }

        if (data.hackBridge === "event") {
            if (data.type === "ready") {
                this.readies++;
            }
            this.dotnet.invokeMethodAsync("OnBridgeEvent", data.type, JSON.stringify(data.payload || {}));
            return;
        }

        if (data.hackBridge === "request") {
            const token = this.token;
            const source = event.source;
            this.dotnet.invokeMethodAsync("OnBridgeRequest", data.type, JSON.stringify(data.payload || {}))
                .then((reply) => JSON.parse(reply || "{}"))
                .catch((error) => ({ error: true, message: String(error) }))
                .then((payload) => {
                    // The tab may have moved on: a reply never reaches another document.
                    if (this.frame && this.frame.contentWindow === source && this.token === token) {
                        this.postToPage(source, { hackBridge: "response", token, id: data.id, payload });
                    }
                });
        }
    }

    // The bridge trusts only messages from its parent window. When the page is shown in a
    // detached display surface (ADR 0078 §3) its parent is that surface's window: the reply goes
    // through the surface's relay so it comes from there. Messages the page posts to its parent
    // are handed to this window by the surface.
    postToPage(source, message) {
        const parent = this.frame ? this.frame.ownerDocument.defaultView : null;
        const relay = parent && parent !== window ? parent.hackerosSurfaceRelay : null;
        if (relay) {
            relay.post(source, message);
        } else {
            source.postMessage(message, "*");
        }
    }

    dispose() {
        window.removeEventListener("message", this.onMessage);
        this.clear();
    }
}

export function attach(host, dotnet) {
    return new PageFrame(host, dotnet);
}
