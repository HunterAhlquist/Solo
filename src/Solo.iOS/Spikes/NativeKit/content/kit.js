// The bridge between kit nodes in the page and the app that draws them.
//
// Kit components on a kit surface render <solo-kit data-kind="…"> elements.
// This serializes them as a tree whenever they change and posts it to the
// app; a tap in the app comes back as an id and becomes a click on the
// element it was made from, which the component's own handler answers.
//
// Outside the app there is nobody to post to, so messages go to the console.

let nextId = 1;
const ids = new WeakMap();
const elements = new Map();

function send(message) {
    const text = JSON.stringify(message);
    const handler = window.webkit?.messageHandlers?.soloKit;
    if (handler) handler.postMessage(text); else console.debug('[kit]', text);
}

function idOf(element) {
    let id = ids.get(element);
    if (!id) { id = nextId++; ids.set(element, id); }
    elements.set(id, new WeakRef(element));
    return id;
}

function node(element) {
    const props = {};
    for (const [name, value] of Object.entries(element.dataset)) if (name !== 'kind') props[name] = value;
    return { id: idOf(element), kind: element.dataset.kind, props, children: nodesIn(element) };
}

// Kit nodes nest through whatever markup lies between them.
function nodesIn(element) {
    const found = [];
    for (const child of element.children) {
        if (child.tagName === 'SOLO-KIT') found.push(node(child)); else found.push(...nodesIn(child));
    }
    return found;
}

export function start(hostId) {
    const host = document.getElementById(hostId);
    let pending = false, trees = 0;
    const flush = () => {
        pending = false;
        elements.clear();
        send({
            type: 'tree', n: ++trees, at: Date.now(),
            roots: [...host.querySelectorAll('[data-kit-root]')].map(root => ({
                seam: root.dataset.kitRoot, project: root.dataset.project, alias: root.dataset.alias, name: root.dataset.name,
                nodes: nodesIn(root),
            })),
        });
    };
    new MutationObserver(() => { if (!pending) { pending = true; queueMicrotask(flush); } })
        .observe(host, { subtree: true, childList: true, attributes: true, characterData: true });

    window.soloKit = {
        // A tap on a node the app drew.
        tap(id) {
            const element = elements.get(id)?.deref();
            if (!element) return false;
            element.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
            return true;
        },
    };

    // A pulse four times a second: the gaps between the ones that arrive say how the system
    // treats a page nobody can see.
    let beat = 0;
    setInterval(() => send({ type: 'beat', n: ++beat, at: Date.now(), visibility: document.visibilityState }), 250);

    send({ type: 'ready', at: Date.now(), sinceNavigation: Math.round(performance.now()) });
    flush();
}

export function open(route) {
    send({ type: 'open', route });
}
