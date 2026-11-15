// The kit surface's bridge (Kit/KitSurface.cs): what an app that draws
// extensions' contributions itself is told, and what it may say back.
//
// The page renders each mounted place's kit components as <solo-kit> nodes.
// This watches them and posts what changed to the app as trees, through the
// one way out a hosted page has (solo.host.post). The other way, the app can
// ask for a place to be mounted, dropped or read again, and can press a node:
// a press becomes a click on the element the node was made from, which the
// component's own handler answers. Nothing else comes in, and a node is only
// ever data going out.

let nextId = 1;
const ids = new WeakMap();
const elements = new Map();
let observer = null;
let dotnet = null;

function idOf(element) {
    let id = ids.get(element);
    if (!id) {
        id = nextId++;
        ids.set(element, id);
    }
    elements.set(id, new WeakRef(element));
    return id;
}

function node(element) {
    const props = {};
    for (const [name, value] of Object.entries(element.dataset)) {
        if (name !== 'kind') props[name] = value;
    }
    return { id: idOf(element), kind: element.dataset.kind, props, children: nodesIn(element) };
}

// Kit nodes nest through whatever markup lies between them; only they are read.
function nodesIn(element) {
    const found = [];
    for (const child of element.children) {
        if (child.localName === 'solo-kit') found.push(node(child));
        else found.push(...nodesIn(child));
    }
    return found;
}

// A unit is what is sent whole when anything in it changes: a mount, or one item of it.
const unitOf = element => element.closest('[data-kit-item], [data-kit-mount]');

function sectionsOf(unit) {
    return [...unit.querySelectorAll('[data-kit-section]')]
        .filter(section => unitOf(section) === unit)
        .map(section => ({
            extension: section.dataset.extension ?? '',
            title: section.dataset.title ?? '',
            order: Number(section.dataset.order ?? 0),
            nodes: nodesIn(section),
        }))
        // A contribution with nothing to say takes no room, here as on the web.
        .filter(section => section.nodes.length > 0);
}

function describe(unit) {
    const mount = unit.closest('[data-kit-mount]');
    if (unit !== mount) {
        return { mount: mount.dataset.kitMount, item: unit.dataset.kitItem, sections: sectionsOf(unit) };
    }
    // The mount itself: its own sections, and which items it has, so the app can forget the ones that left.
    const items = [...mount.querySelectorAll('[data-kit-item]')];
    return {
        mount: mount.dataset.kitMount,
        sections: sectionsOf(mount),
        items: items.map(item => ({ item: item.dataset.kitItem, sections: sectionsOf(item) })),
    };
}

function forget() {
    for (const [id, reference] of elements) {
        if (!reference.deref()?.isConnected) elements.delete(id);
    }
}

export function start(reference, hostId) {
    dotnet = reference;
    const host = document.getElementById(hostId);
    const dirty = new Set();
    let pending = false;

    const flush = () => {
        pending = false;
        const units = [...dirty].filter(unit => unit.isConnected);
        dirty.clear();
        // A mount being sent whole carries its items, so they need not go twice.
        const whole = new Set(units.filter(unit => unit.hasAttribute('data-kit-mount')));
        const described = units
            .filter(unit => whole.has(unit) || !whole.has(unit.closest('[data-kit-mount]')))
            .map(describe);
        if (described.length > 0) window.solo.host.post('kit', { units: described });
        if (elements.size > 4000) forget();
    };

    const mark = element => {
        if (!element || element === host || !host.contains(element)) return;
        const unit = unitOf(element);
        if (!unit) return;
        dirty.add(unit);
        if (!pending) {
            pending = true;
            queueMicrotask(flush);
        }
    };

    observer = new MutationObserver(records => {
        for (const record of records) {
            if (record.target === host) {
                // Mounts came or went: the ones that came are new units; the app knows what it dropped.
                record.addedNodes.forEach(added => added.nodeType === 1 && mark(added));
            } else if (record.type === 'childList' && record.target.hasAttribute?.('data-kit-mount')) {
                // Items came or went: the mount is sent whole, so the list of them is current.
                mark(record.target);
            } else {
                mark(record.target.nodeType === 1 ? record.target : record.target.parentElement);
            }
        }
    });
    observer.observe(host, { subtree: true, childList: true, attributes: true, characterData: true });

    window.solo.kitSurface = {
        // From the app: {"op":"mount"|"unmount"|"refresh", ...}, as text.
        receive(json) {
            return dotnet?.invokeMethodAsync('Receive', String(json));
        },

        // From the app: a node it drew was pressed.
        press(id) {
            const element = elements.get(Number(id))?.deref();
            if (!element?.isConnected || element.dataset.disabled) return false;
            element.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
            return true;
        },
    };

    // The extensions the app draws itself: their contributions are not run here.
    return Array.isArray(window.soloHost?.baked) ? window.soloHost.baked.map(String) : [];
}

// Which places any extension contributes to. Said once extensions have loaded, and again if that changes.
export function announce(seams) {
    window.solo.host.post('kitReady', { seams });
}

export function taskChanged(task) {
    window.solo.host.post('taskChanged', { task });
}

export function stop() {
    observer?.disconnect();
    observer = null;
    dotnet = null;
    delete window.solo.kitSurface;
}
