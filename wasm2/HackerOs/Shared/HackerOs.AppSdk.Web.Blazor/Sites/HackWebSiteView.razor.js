// Link interception of a Blazor site's page area (ADR 0084 §5).
//
// Only what C# cannot reach lives here: catching a click on an <a href> before the OS's own
// Blazor router sees it. Blazor listens on the document (bubbling phase); this listener runs on
// the page area in the capture phase, stops the event there and hands the href to
// HackWebSiteView.OnLinkClicked, which decides between a navigation within the site (the tab's
// address changes, no request) and a navigation of the browser.

class SiteLinks {
    constructor(element, dotnet) {
        this.element = element;
        this.dotnet = dotnet;
        this.onClick = (event) => this.handle(event);
        element.addEventListener("click", this.onClick, true);
    }

    handle(event) {
        // Modified clicks and non-primary buttons keep their meaning (no new tab: one tab per window,
        // the browser says so when it matters); a download attribute is not a navigation.
        if (event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) {
            return;
        }

        const anchor = event.target instanceof Element ? event.target.closest("a[href]") : null;
        if (!anchor || !this.element.contains(anchor) || anchor.hasAttribute("download")) {
            return;
        }

        const href = anchor.getAttribute("href");
        if (!href || href.startsWith("javascript:")) {
            return;
        }

        // As in Blazor, a link that targets another frame (_top, _blank, a name) is not a navigation
        // within the site: the document changes. One tab per window, so it opens in this tab.
        const target = anchor.getAttribute("target");
        const forceLoad = !!target && target !== "_self";

        // The OS's Blazor router must never see a site link: it would navigate Kazo OS itself.
        event.preventDefault();
        event.stopPropagation();
        event.stopImmediatePropagation();
        this.dotnet.invokeMethodAsync("OnLinkClicked", href, forceLoad);
    }

    dispose() {
        this.element.removeEventListener("click", this.onClick, true);
    }
}

export function attach(element, dotnet) {
    return new SiteLinks(element, dotnet);
}
