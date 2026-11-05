// Home page: asks the site process how it is doing (GET /health) and shows since when it runs.
// The request leaves the isolated page through the browser's bridge and the simulated network,
// so it shows in the Network tab of the developer tools (F12) like any fetch.
(function () {
    "use strict";
    const target = document.getElementById("site-health");
    if (!target) {
        return;
    }

    fetch(target.dataset.healthUrl || "/health", { headers: { "Accept": "application/json" } })
        .then((response) => {
            if (!response.ok) {
                throw new Error("HTTP " + response.status);
            }
            return response.json();
        })
        .then((status) => {
            const started = new Date(status.startedAt);
            target.textContent = status.status;
            target.title = "running since " + started.toLocaleTimeString();
            target.classList.add("ok");
            console.log("intranet: site up since " + started.toISOString());
        })
        .catch((error) => {
            target.textContent = "down";
            target.classList.add("ko");
            console.error("intranet: /health failed", error);
        });
})();
