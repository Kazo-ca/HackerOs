// Staff directory: filters as the user types by calling GET /api/people?q= (JSON) and redraws the
// table. Without script, the form's GET does the same on the server. The address bar follows the
// search through history.replaceState, which the browser relays to its address bar.
(function () {
    "use strict";
    const form = document.getElementById("people-search");
    const input = document.getElementById("people-query");
    const rows = document.getElementById("people-rows");
    const count = document.getElementById("people-count");
    if (!form || !input || !rows || !count) {
        return;
    }

    const api = form.dataset.api || "/api/people";
    let pending = 0;

    function cell(text, className) {
        const td = document.createElement("td");
        if (className) {
            const span = document.createElement(className === "code" ? "code" : "span");
            if (className !== "code") {
                span.className = className;
            }
            span.textContent = text;
            td.appendChild(span);
        } else {
            td.textContent = text;
        }
        return td;
    }

    function render(people) {
        rows.replaceChildren(...people.map((person) => {
            const tr = document.createElement("tr");
            tr.append(cell(person.name), cell(person.role), cell(person.team, "tag"), cell(person.extension, "code"), cell("@" + person.handle));
            return tr;
        }));
        count.textContent = people.length + " result(s)";
    }

    async function search() {
        const query = input.value.trim();
        const ticket = ++pending;
        try {
            const response = await fetch(api + "?q=" + encodeURIComponent(query), { headers: { "Accept": "application/json" } });
            if (!response.ok) {
                throw new Error("HTTP " + response.status);
            }
            const people = await response.json();
            if (ticket === pending) {
                // Only the answer to the last key stroke is drawn.
                render(people);
                history.replaceState(null, "", query ? "?q=" + encodeURIComponent(query) : location.pathname);
            }
        } catch (error) {
            console.error("intranet: people search failed", error);
        }
    }

    input.addEventListener("input", search);
    form.addEventListener("submit", (event) => {
        event.preventDefault();
        search();
    });
})();
