// Notes list: the star of a card pins or unpins the note with PATCH /api/notes/{id} (JSON body),
// then updates the card in place. Pinned notes come first the next time the list is rendered.
(function () {
    "use strict";
    const list = document.getElementById("notes-list");
    if (!list) {
        return;
    }

    const api = list.dataset.api || "/api/notes";

    list.addEventListener("click", async (event) => {
        const button = event.target instanceof Element ? event.target.closest("button.pin") : null;
        if (!button) {
            return;
        }

        const card = button.closest("[data-note-id]");
        const pinned = button.dataset.pinned !== "true";
        button.disabled = true;
        try {
            const response = await fetch(api + "/" + card.dataset.noteId, {
                method: "PATCH",
                headers: { "Content-Type": "application/json", "Accept": "application/json" },
                body: JSON.stringify({ pinned: pinned })
            });
            if (!response.ok) {
                throw new Error("HTTP " + response.status);
            }
            const note = await response.json();
            button.dataset.pinned = note.pinned ? "true" : "false";
            button.textContent = note.pinned ? "★" : "☆";
            card.classList.toggle("pinned", note.pinned);
        } catch (error) {
            console.error("intranet: pin failed", error);
        } finally {
            button.disabled = false;
        }
    });
})();
