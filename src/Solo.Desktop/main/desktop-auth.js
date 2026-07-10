// Google refuses to run its sign-in pages inside Electron ("this browser or
// app may not be secure"), no matter how Chrome-like the user agent looks. So
// the desktop shell signs in the way Google prescribes for installed apps: in
// the user's real browser. The shell serves a page on its own local origin —
// the same http://localhost:{port} the operator already registered with
// Google — that runs the ordinary GIS flow and posts the resulting ID token
// back. The shell then hands that credential to the running client exactly
// where a GIS callback would have delivered it, so the client neither knows
// nor cares which browser did the signing in.
//
// Each flow is armed with a single-use, expiring `state` nonce minted when
// the user clicks the in-app button. Without a live nonce the completion
// endpoint refuses — nothing on the machine can push a credential into the
// app unless the app just asked for one.
const crypto = require('crypto');

const STATE_TTL_MS = 10 * 60 * 1000;

class DesktopAuth {
    constructor() {
        this.pending = new Map(); // state -> expiry epoch ms
    }

    /** Starts a flow: returns the state nonce for the browser sign-in URL. */
    mint() {
        this.sweep();
        const state = crypto.randomBytes(16).toString('hex');
        this.pending.set(state, Date.now() + STATE_TTL_MS);
        return state;
    }

    /** True while the state may still complete (does not consume it). */
    isPending(state) {
        this.sweep();
        return typeof state === 'string' && this.pending.has(state);
    }

    /** Consumes the state; a second completion with the same nonce fails. */
    consume(state) {
        this.sweep();
        return typeof state === 'string' && this.pending.delete(state);
    }

    sweep() {
        const now = Date.now();
        for (const [state, expiry] of this.pending) {
            if (expiry < now) {
                this.pending.delete(state);
            }
        }
    }
}

/** Shallow shape check only — real validation is the API's signature check. */
function looksLikeJwt(credential) {
    return typeof credential === 'string'
        && credential.length < 8192
        && /^[\w-]+\.[\w-]+\.[\w-]+$/.test(credential);
}

module.exports = { DesktopAuth, looksLikeJwt };
