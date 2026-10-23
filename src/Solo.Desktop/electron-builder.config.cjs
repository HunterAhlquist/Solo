const { build, version } = require('./package.json');
const { identityForVersion } = require('./main/app-identity');
const identity = identityForVersion(version);

module.exports = {
    ...build,
    appId: identity.appId,
    productName: identity.productName,
    // The installed package, not the build runner's environment, decides its
    // profile and branding every time it starts.
    extraMetadata: { productName: identity.productName },
    ...(identity.preview ? {
        win: { ...build.win, icon: 'build/icon-preview.png' },
        linux: { ...build.linux, icon: 'build/icon-preview.png', executableName: identity.executableName },
    } : {}),
};
