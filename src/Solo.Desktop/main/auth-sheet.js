// On macOS, the instance's sign-in page opens in Apple's web authentication
// sheet over the Solo window rather than in a browser tab. Google allows its
// sign-in there (it is the system browser, not an embedded view), and the
// sheet shares the browser's cookies, so an existing Google sign-in carries
// over. The loopback flow in desktop-auth.js is unchanged: the sheet is only
// a different place to show the same page.
//
// The native half is native/auth-session (built by `npm run build:native`).
// Anywhere it is missing — Windows, Linux, an unbuilt checkout — sign-in falls
// back to the default browser.

// The sheet closes itself when the page navigates to this scheme; the
// loopback listener redirects there once the session is redeemed. Nothing
// else registers or handles it, so it carries no data.
const CALLBACK_SCHEME = 'com.solo.desktop';
const COMPLETION_URL = `${CALLBACK_SCHEME}://desktop-auth/done`;

let native;
let nextId = 1;
let activeId = null;

function nativeModule() {
    if (native !== undefined) {
        return native;
    }

    native = null;
    if (process.platform !== 'darwin') {
        return native;
    }

    try {
        native = require('../native/auth-session/build/Release/auth_session.node');
    } catch (err) {
        console.warn('Solo sign-in sheet unavailable; signing in through the browser.', err.message);
    }
    return native;
}

/**
 * Opens url in the sign-in sheet over win. Returns null when the sheet can't
 * be shown (the caller falls back to the browser); otherwise a handle whose
 * completionUrl closes the sheet when the page navigates to it.
 * onDismissed(reason) fires when the sheet closes without reaching
 * completionUrl: "cancelled" for the user's Cancel or cancel() itself.
 */
function presentAuthSheet(url, win, onDismissed) {
    const module = nativeModule();
    if (!module || !win || win.isDestroyed()) {
        return null;
    }

    const id = nextId++;
    let started;
    try {
        started = module.start(url, CALLBACK_SCHEME, win.getNativeWindowHandle(), (error, callbackUrl) => {
            if (activeId === id) {
                activeId = null;
            }
            if (!callbackUrl) {
                onDismissed(error ?? 'cancelled');
            }
        });
    } catch (err) {
        console.warn('Solo sign-in sheet failed to open; signing in through the browser.', err.message);
        return null;
    }
    if (!started) {
        return null;
    }

    activeId = id;
    return {
        completionUrl: COMPLETION_URL,
        // Only this sheet: a later sign-in may have replaced it already.
        cancel: () => {
            if (activeId === id) {
                module.cancel();
            }
        },
    };
}

module.exports = { presentAuthSheet };
