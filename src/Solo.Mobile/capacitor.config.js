// The app loads the Solo instance itself rather than a bundled copy of the
// client: the client and the extensions it loads from the server use
// same-origin relative URLs throughout (REST, SignalR, images, extension
// assets), which only resolve against the instance's own origin.
const instanceUrl = process.env.SOLO_INSTANCE_URL;
const commandsWritingNativeConfig = ['add', 'copy', 'sync', 'update', 'run'];
if (!instanceUrl && commandsWritingNativeConfig.includes(process.argv[2])) {
    throw new Error('Set SOLO_INSTANCE_URL to the Solo instance this build opens, e.g. https://solo.example.com');
}

const instance = instanceUrl ? new URL(instanceUrl) : null;

/** @type {import('@capacitor/cli').CapacitorConfig} */
module.exports = {
    appId: 'io.github.hunterahlquist.solo',
    appName: 'Solo',
    webDir: 'www',
    server: instance && {
        url: instance.origin + instance.pathname.replace(/\/?$/, '/'),
        cleartext: instance.protocol === 'http:',
        errorPath: 'error.html',
    },
    ios: {
        contentInset: 'always',
    },
};
