(() => {
    "use strict";
    const picker = document.getElementById("approver-picker");
    if (!picker) return;
    const ids = document.getElementById("approver-ids");
    const search = document.getElementById("approver-search");
    const selectedList = document.getElementById("selected-approvers");
    const results = document.getElementById("approver-results");
    const status = document.getElementById("picker-status");
    const required = document.getElementById("required-approvals");
    const selected = new Map(ids.value.split(",").filter(value => value.trim()).map(value => {
        const id = BigInt(value.trim()).toString();
        return [id, { id, login: null }];
    }));
    let timer;
    let controller;
    let revision = 0;
    let matches = [];

    function validate() {
        search.setCustomValidity(selected.size >= Math.max(2, Number(required.value) || 2)
            ? "" : "Select at least as many distinct people as the required approval count (minimum two).");
    }

    function personRow(person, action) {
        const row = document.createElement("li");
        const label = document.createElement("span");
        label.textContent = person.login ? `@${person.login}` : "GitHub user";
        const id = document.createElement("small");
        id.textContent = `ID ${person.id}`;
        label.append(id);
        const button = document.createElement("button");
        button.type = "button";
        button.textContent = action;
        button.setAttribute("aria-label", `${action} ${person.login ? "@" + person.login : "GitHub user " + person.id}`);
        button.addEventListener("click", () => {
            if (action === "Remove") selected.delete(person.id);
            else selected.set(person.id, person);
            render();
            search.focus();
        });
        row.append(label, button);
        return row;
    }

    function render() {
        selectedList.replaceChildren(...Array.from(selected.values(), person => personRow(person, "Remove")));
        const available = matches.filter(person => !selected.has(person.id));
        results.replaceChildren(...available.map(person => personRow(person, "Add")));
        if (matches.length) status.textContent = available.length
            ? "Select Add to include a person. Showing up to 10 matches."
            : "All matching users are already selected.";
        ids.value = Array.from(selected.keys()).join(",");
        validate();
    }

    async function lookup(parameters, signal) {
        const response = await fetch(`/settings/people?${new URLSearchParams(parameters)}`, {
            signal, headers: { Accept: "application/json" }, credentials: "same-origin", redirect: "error"
        });
        if (!response.ok) {
            const messages = {
                400: "Use 2-80 letters, numbers, spaces, dots, underscores or hyphens.",
                401: "Your session expired. Sign in again to look up people.",
                403: "Only current approvers may look up people.",
                404: "GitHub user not found.",
                503: "GitHub lookup is unavailable or rate-limited. Try again later or contact the portal operator."
            };
            throw new Error(messages[response.status] || "GitHub lookup failed. Try again later.");
        }
        return response.json();
    }

    search.addEventListener("keydown", event => {
        if (event.key === "Enter" || event.key === "ArrowDown") {
            event.preventDefault();
            results.querySelector("button")?.focus();
        }
    });
    search.addEventListener("input", () => {
        clearTimeout(timer);
        controller?.abort();
        const current = ++revision;
        const query = search.value.trim();
        matches = [];
        render();
        if (query.length < 2) {
            status.textContent = "Type at least two characters to search.";
            return;
        }
        status.textContent = "Searching GitHub...";
        timer = setTimeout(async () => {
            controller = new AbortController();
            try {
                const people = await lookup({ q: query }, controller.signal);
                if (current !== revision) return;
                matches = people;
                render();
                if (!people.length) status.textContent = "No matching GitHub users. Try an exact username; Enterprise accounts may not appear in search.";
            } catch (error) {
                if (current !== revision || error.name === "AbortError") return;
                status.textContent = error.message;
            }
        }, 500);
    });
    required.addEventListener("input", validate);
    ids.type = "hidden";
    document.getElementById("approver-ids-label").hidden = true;
    document.getElementById("approver-help").hidden = true;
    picker.hidden = false;
    render();
    status.textContent = "Loading selected GitHub users...";
    (async () => {
        let failed = false;
        for (const id of Array.from(selected.keys())) {
            try {
                const [person] = await lookup({ id });
                if (selected.has(id)) selected.set(id, person);
                render();
            } catch (error) {
                failed = true;
                if (revision === 0) status.textContent = `${error.message} Existing IDs are preserved and can still be removed.`;
                break;
            }
        }
        if (!failed && revision === 0) status.textContent = "Type at least two characters to search.";
    })();
})();
