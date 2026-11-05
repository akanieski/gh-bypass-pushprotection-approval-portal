(() => {
    "use strict";
    const picker = document.getElementById("approver-picker");
    if (!picker) return;
    const ids = document.getElementById("approver-ids");
    const search = document.getElementById("approver-search");
    const selectedRows = document.querySelector("#selected-approvers tbody");
    const results = document.getElementById("approver-results");
    const status = document.getElementById("picker-status");
    const required = document.getElementById("required-approvals");
    const selected = new Map(ids.value.split(",").filter(value => value.trim()).map(value => {
        const id = BigInt(value.trim()).toString();
        return [id, { id, login: null }];
    }));
    let controller;
    let timer;
    let revision = 0;
    let matches = [];
    let activeIndex = -1;

    function validate() {
        search.setCustomValidity(selected.size >= Math.max(2, Number(required.value) || 2)
            ? "" : "Select at least as many distinct people as the required approval count (minimum two).");
    }

    function personIdentity(person) {
        const identity = document.createElement("span");
        identity.className = "person-identity";
        const name = document.createElement("span");
        name.textContent = person.login ? `@${person.login}` : "GitHub user";
        const icon = document.createElement("span");
        icon.className = "person-avatar";
        icon.textContent = (person.login || "?").slice(0, 1).toUpperCase();
        icon.setAttribute("aria-hidden", "true");
        if (person.avatarUrl) {
            const image = document.createElement("img");
            image.className = "person-avatar";
            image.alt = "";
            image.width = 28;
            image.height = 28;
            image.referrerPolicy = "no-referrer";
            image.addEventListener("error", () => {
                icon.title = "Avatar unavailable";
                image.replaceWith(icon);
            }, { once: true });
            image.src = person.avatarUrl;
            identity.append(image);
        } else identity.append(icon);
        identity.append(name);
        return identity;
    }

    function personRow(person) {
        const row = document.createElement("tr");
        const label = document.createElement("td");
        label.append(personIdentity(person));
        const id = document.createElement("td");
        id.textContent = person.id;
        const actions = document.createElement("td");
        const button = document.createElement("button");
        button.type = "button";
        button.textContent = "Remove";
        button.setAttribute("aria-label", `Remove ${person.login ? "@" + person.login : "GitHub user " + person.id}`);
        button.addEventListener("click", () => {
            selected.delete(person.id);
            renderSelected();
            search.focus();
            renderSuggestions();
            status.textContent = `${person.login ? "@" + person.login : "GitHub user " + person.id} removed. Changes apply when you save settings.`;
        });
        actions.append(button);
        row.append(label, id, actions);
        return row;
    }

    function renderSelected() {
        selectedRows.replaceChildren(...Array.from(selected.values(), personRow));
        if (!selected.size) {
            const row = document.createElement("tr");
            const cell = document.createElement("td");
            cell.colSpan = 3;
            cell.textContent = "No approvers selected.";
            row.append(cell);
            selectedRows.append(row);
        }
        ids.value = Array.from(selected.keys()).join(",");
        validate();
    }

    function closeSuggestions() {
        results.hidden = true;
        search.setAttribute("aria-expanded", "false");
        search.removeAttribute("aria-activedescendant");
        activeIndex = -1;
        for (const option of results.children) option.setAttribute("aria-selected", "false");
    }

    function cancelLookup() {
        clearTimeout(timer);
        controller?.abort();
        revision++;
        search.setAttribute("aria-busy", "false");
        closeSuggestions();
    }

    function selectPerson(person) {
        selected.set(person.id, person);
        cancelLookup();
        matches = [];
        search.value = "";
        renderSelected();
        status.textContent = `@${person.login} added. Changes apply when you save settings.`;
        search.focus();
    }

    function renderSuggestions() {
        closeSuggestions();
        const available = matches.filter(person => !selected.has(person.id));
        results.replaceChildren(...available.map(person => {
            const option = document.createElement("li");
            option.id = `approver-option-${person.id}`;
            option.setAttribute("role", "option");
            option.setAttribute("aria-selected", "false");
            const id = document.createElement("small");
            id.textContent = `ID ${person.id}`;
            option.append(personIdentity(person), id);
            option.addEventListener("mousedown", event => event.preventDefault());
            option.addEventListener("click", () => selectPerson(person));
            return option;
        }));
        const open = available.length > 0 && document.activeElement === search;
        results.hidden = !open;
        search.setAttribute("aria-expanded", String(open));
    }

    function moveActive(direction) {
        if (!results.children.length) return;
        results.hidden = false;
        search.setAttribute("aria-expanded", "true");
        activeIndex = activeIndex < 0
            ? (direction > 0 ? 0 : results.children.length - 1)
            : (activeIndex + direction + results.children.length) % results.children.length;
        for (const [index, option] of Array.from(results.children).entries())
            option.setAttribute("aria-selected", String(index === activeIndex));
        const option = results.children[activeIndex];
        search.setAttribute("aria-activedescendant", option.id);
        option.scrollIntoView({ block: "nearest" });
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

    async function runLookup(query, current) {
        status.textContent = "Looking up GitHub users...";
        search.setAttribute("aria-busy", "true");
        controller = new AbortController();
        try {
            const people = await lookup({ q: query }, controller.signal);
            if (current !== revision) return;
            matches = people;
            renderSuggestions();
            const count = matches.filter(person => !selected.has(person.id)).length;
            status.textContent = count
                ? `${count} suggestions. Use the arrow keys and Enter to select.`
                : people.length ? "All matching users are already selected."
                : "No matching GitHub users. Try an exact username; Enterprise accounts may not appear in search.";
        } catch (error) {
            if (current !== revision || error.name === "AbortError") return;
            status.textContent = error.message;
        } finally {
            if (current === revision) search.setAttribute("aria-busy", "false");
        }
    }

    function scheduleLookup() {
        cancelLookup();
        matches = [];
        results.replaceChildren();
        const query = search.value.trim();
        if (query.length < 2) {
            status.textContent = "Type at least two characters to find GitHub users.";
            return;
        }
        status.textContent = "Waiting to look up GitHub users...";
        const current = revision;
        timer = setTimeout(() => void runLookup(query, current), 500);
    }

    search.addEventListener("keydown", event => {
        if (event.isComposing) return;
        if (event.key === "Enter") {
            event.preventDefault();
            if (!results.hidden && activeIndex >= 0) results.children[activeIndex].click();
        } else if (event.key === "ArrowDown" || event.key === "ArrowUp") {
            event.preventDefault();
            moveActive(event.key === "ArrowDown" ? 1 : -1);
        } else if (event.key === "Escape") {
            event.preventDefault();
            cancelLookup();
            status.textContent = "Suggestions closed.";
        }
    });
    search.addEventListener("input", event => {
        if (!event.isComposing) scheduleLookup();
    });
    search.addEventListener("compositionstart", cancelLookup);
    search.addEventListener("compositionend", scheduleLookup);
    search.addEventListener("focus", renderSuggestions);
    search.addEventListener("blur", cancelLookup);
    required.addEventListener("input", validate);
    ids.type = "hidden";
    document.getElementById("approver-ids-label").hidden = true;
    document.getElementById("approver-help").hidden = true;
    picker.hidden = false;
    renderSelected();
    status.textContent = "Loading selected GitHub users...";
    (async () => {
        let failed = false;
        for (const id of Array.from(selected.keys())) {
            try {
                const [person] = await lookup({ id });
                if (selected.has(id)) selected.set(id, person);
                renderSelected();
            } catch (error) {
                failed = true;
                if (revision === 0) status.textContent = `${error.message} Existing IDs are preserved and can still be removed.`;
                break;
            }
        }
        if (!failed && revision === 0) status.textContent = "Type at least two characters to find GitHub users.";
    })();
})();
