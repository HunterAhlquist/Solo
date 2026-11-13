// Which local port an instance opens on. The port is the instance's origin,
// and the origin holds what the client stored for it — its session, and any
// offline edits still waiting to sync — so the choice is deliberately sticky.
//
// Each instance owns a list of ports (`ports`, main port first) and opens on
// `port`, the one it last opened on. When something else on the machine holds
// that port, it tries the rest of its own, then a new one nobody else owns,
// and stays wherever it opened: moving back just because the old port came
// free would strand whatever was stored on the new origin in turn.
//
// No two instances in the list ever share a port. A removed instance's ports
// can go to a new one, but main.js clears an origin's storage when its
// instance is removed and again before a port is first used, so nothing
// carries over.
//
// The range is the desktop's own, 100 ports from settings.port (41321): the
// iPhone and iPad app hands out ports from 43321, and an iOS simulator or the
// iPad app on a Mac shares this machine's loopback.
//
// No Electron here, so scripts/check-instance-ports.mjs can drive it.
const RANGE = 100;

// The ports an instance owns, main port first. Settings written before
// fallbacks existed have only `port`.
function ownedPorts(instance) {
    const owned = Array.isArray(instance.ports) ? instance.ports : [];
    return [...new Set(instance.port ? [...owned, instance.port] : owned)];
}

// One port choice at a time per instance: two windows opening it at once
// would otherwise each pick a port while sharing one server, and save one it
// never bound.
const inFlight = new WeakMap();

/**
 * Starts the instance on one of its ports, or a new one.
 *
 *   start(port)   resolves once listening there — with the port it is really
 *                 listening on, when a server was already running — and
 *                 rejects with code EADDRINUSE when the port is held, or
 *                 anything else to stop.
 *   prepare(port) runs before a port new to this instance is first tried.
 *
 * Updates instance.port and instance.ports, and resolves with
 * { port, moved }: moved when it opened anywhere but the port it last used
 * (never for a new instance). Rejects when no port in range can be had.
 */
function openOnPort(instance, instances, options) {
    const running = inFlight.get(instance);
    if (running) {
        // Same server, same port; only the first caller reports a move.
        return running.then(({ port }) => ({ port, moved: false }));
    }
    const choosing = choosePort(instance, instances, options).finally(() => inFlight.delete(instance));
    inFlight.set(instance, choosing);
    return choosing;
}

async function choosePort(instance, instances, { first, start, prepare = async () => {} }) {
    const owned = ownedPorts(instance);
    const previous = instance.port ?? null;
    const candidates = previous ? [previous, ...owned.filter(port => port !== previous)] : owned;

    const opened = port => {
        instance.port = port;
        instance.ports = owned.includes(port) ? owned : [...owned, port];
        return { port, moved: previous !== null && port !== previous };
    };

    for (const port of candidates) {
        const bound = await tryStart(start, port);
        if (bound) {
            return opened(bound);
        }
    }

    const taken = new Set(instances.filter(other => other !== instance).flatMap(ownedPorts));
    for (let port = first; port < Math.min(first + RANGE, 65536); port++) {
        if (taken.has(port) || owned.includes(port)) {
            continue;
        }
        await prepare(port);
        const bound = await tryStart(start, port);
        if (bound) {
            return opened(bound);
        }
    }
    throw new Error(`no free local port from ${first} to ${Math.min(first + RANGE, 65536) - 1}`);
}

// The port it is listening on, or null when that one is held.
async function tryStart(start, port) {
    try {
        const bound = await start(port);
        return Number.isInteger(bound) ? bound : port;
    } catch (err) {
        if (err?.code === 'EADDRINUSE') {
            return null;
        }
        throw err;
    }
}

module.exports = { openOnPort, ownedPorts, RANGE };
