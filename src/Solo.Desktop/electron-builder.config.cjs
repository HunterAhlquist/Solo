const { build, version } = require('./package.json');
const { identityForVersion } = require('./main/app-identity');
const identity = identityForVersion(version);

module.exports = {
    ...build,
    appId: identity.appId,
    afterPack: 'scripts/after-pack.mjs',
    mac: {
        ...build.mac,
        extendInfo: {
            ...build.mac.extendInfo,
            NSLocalNetworkUsageDescription: 'Solo connects to the Solo instances you add on your local network to sign in and sync your projects.',
        },
    },
    productName: identity.productName,
    // The installed package, not the build runner's environment, decides its
    // profile and branding every time it starts.
    extraMetadata: { productName: identity.productName },
    ...(identity.preview ? {
        win: { ...build.win, icon: 'build/icon-preview.png' },
        linux: { ...build.linux, icon: 'build/icon-preview.png', executableName: identity.executableName },
    } : {}),
};
