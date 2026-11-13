// The local servers, one per open instance, by instance id. One is started
// the first time its instance is opened and then left running: it is an idle
// listener, and keeping it means a window can be reopened without the port
// being contended.
//
// Every start goes through the port choice (main/ports.js), even when the
// server is already running, so a window never reads instance.port while
// another open is still choosing it: the choice runs once per instance, and a
// running server answers it at once with the port it listens on.
//
// No Electron here, so scripts/check-instance-ports.mjs can drive the same
// path main.js does with a stand-in server.
const ports = require('./ports');

function createLocalServers({ createServer, instances, firstPort, prepare = async () => {} }) {
    //   instance id → { server, port, listening: Promise<port> }
    const servers = new Map();

    function ensure(instance, port) {
        let entry = servers.get(instance.id);
        if (!entry) {
            const server = createServer(instance);
            const listening = new Promise((resolve, reject) => {
                server.once('error', reject);
                server.listen(port, '127.0.0.1', () => {
                    server.removeListener('error', reject);
                    resolve(port);
                });
            });
            // Shared by everyone opening the instance at once; a failed start
            // is forgotten before they hear of it, so a retry starts over
            // instead of awaiting the same rejection forever.
            entry = {
                server,
                port,
                listening: listening.catch(err => {
                    if (servers.get(instance.id) === entry) {
                        servers.delete(instance.id);
                    }
                    throw err;
                }),
            };
            servers.set(instance.id, entry);
        }
        // The port it really listens on, whichever the caller asked for.
        return entry.listening;
    }

    /**
     * Starts the instance's server, or finds it running. Resolves once it
     * listens, with instance.port already the port it listens on, and
     * { port, moved, changed }: changed when instance.port or instance.ports
     * differ from before, so the settings need writing.
     */
    async function start(instance) {
        const before = JSON.stringify([instance.port, ports.ownedPorts(instance)]);
        const { port, moved } = await ports.openOnPort(instance, instances(), {
            first: firstPort(),
            start: candidate => ensure(instance, candidate),
            prepare,
        });
        return { port, moved, changed: JSON.stringify([instance.port, instance.ports]) !== before };
    }

    function stop(instance) {
        const entry = servers.get(instance.id);
        if (!entry) {
            return;
        }
        servers.delete(instance.id);
        entry.server.close();
        entry.server.closeAllConnections?.();
    }

    return { start, stop };
}

module.exports = { createLocalServers };
