// The app loads the Solo instance itself rather than a bundled copy of the
// client: the client and the extensions it loads from the server use
// same-origin relative URLs throughout (REST, SignalR, images, extension
// assets), which only resolve against the instance's own origin.
// Optional: the instance the build opens first, also added to the app's list
// of instances. Without one, the app opens on that list.
const instanceUrl = process.env.SOLO_INSTANCE_URL;
const instance = instanceUrl ? new URL(instanceUrl) : null;

/** @type {import('@capacitor/cli').CapacitorConfig} */
module.exports = {
    appId: 'com.hunterahlquist.solo',
    appName: 'Solo',
    webDir: 'www',
    ...(instance && {
        server: {
            url: instance.origin + instance.pathname.replace(/\/?$/, '/'),
            cleartext: instance.protocol === 'http:',
        },
    }),
    ios: {
        // The page reaches every edge and keeps its own content clear of the
        // status bar, the island and the home indicator (viewport-fit=cover and
        // env(safe-area-inset-*) in the client), so the app bar's colour runs
        // up behind the status bar the same way it does in Safari.
        contentInset: 'never',
    },
};
