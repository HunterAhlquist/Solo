const fs = require('fs');
const path = require('path');

function identityForVersion(version) {
    const preview = version.includes('-');
    return {
        preview,
        productName: preview ? 'Solo (Preview)' : 'Solo',
        appId: preview ? 'com.solo.desktop.preview' : 'com.solo.desktop',
        executableName: preview ? 'solo-preview' : 'solo-desktop',
        iconSource: preview ? 'Solo Preview.icon' : 'Solo.icon',
    };
}

function configureAppIdentity(app, env = process.env) {
    const identity = identityForVersion(app.getVersion());
    if (identity.preview) app.setName(identity.productName);
    const userData = env.SOLO_USER_DATA_DIR || (identity.preview
        ? path.join(app.getPath('appData'), identity.productName) : null);
    if (userData) {
        fs.mkdirSync(userData, { recursive: true });
        // Chromium resolves sessionData independently of userData. Set both
        // before ready and before the process lock so channels can coexist.
        app.setPath('userData', userData);
        app.setPath('sessionData', userData);
        app.setAppLogsPath(path.join(userData, 'logs'));
        const crashDumps = path.join(userData, 'Crashpad');
        fs.mkdirSync(crashDumps, { recursive: true });
        app.setPath('crashDumps', crashDumps);
    }
    return identity;
}

module.exports = { identityForVersion, configureAppIdentity };
