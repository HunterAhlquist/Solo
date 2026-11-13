const { build, version } = require('./package.json');
const { identityForVersion } = require('./main/app-identity');
const identity = identityForVersion(version);

// Named here rather than left to electron-builder's defaults so the file on
// disk and the release asset carry the same name: build/updater-feed.mjs hashes
// the local file and writes its download URL into the feed, and GitHub rewrites
// a space in an asset name to a dot. `${…}` reaches electron-builder as a
// literal placeholder for it to expand.
const artifactName = `${identity.artifactBase}-\${version}-\${arch}.\${ext}`;

// Where the installed app looks for updates. electron-builder bakes this into
// the package as app-update.yml, so it has to be right at packaging time — long
// before any update-checking code reads it.
//
// `generic` rather than `github`, over a GitHub URL, because the GitHub provider
// rebuilds every download URL from the release tag it found the feed on. This
// release train rebuilds the installers only when the desktop changed, so the
// newest release's feed often has to point back at an earlier release's assets,
// and only a generic feed's absolute URLs survive that. `releases/latest/
// download/` is GitHub's redirect to the newest non-prerelease release, which
// build/updater-feed.mjs guarantees always carries a channel file.
//
// Preview builds get no feed at all: they are prereleases, which that redirect
// skips, so the feed they would read describes the stable app. Previews are
// installed by hand (docs/prereleases.md).
const UPDATE_FEED = 'https://github.com/hunterahlquist/Solo/releases/latest/download/';

module.exports = {
    ...build,
    appId: identity.appId,
    afterPack: 'scripts/after-pack.mjs',
    mac: {
        ...build.mac,
        artifactName,
        extendInfo: {
            ...build.mac.extendInfo,
            NSLocalNetworkUsageDescription: 'Solo connects to the Solo instances you add on your local network to sign in and sync your projects.',
            // Only HTTPS, which is exempt, so App Store Connect needn't ask
            // about export compliance for every upload. electron-builder reads
            // extendInfo from `mac` alone, so it's here rather than under `mas`,
            // and the downloaded app carries it unread.
            ITSAppUsesNonExemptEncryption: false,
        },
    },
    // The Mac App Store build (`--mac mas`, release-mac-store.yml), which Apple
    // treats as another app: the store bundle id, the App Sandbox instead of the
    // hardened runtime, and updates from the App Store or TestFlight rather than
    // electron-updater, which Electron's MAS build doesn't include (process.mas
    // turns it off in main/updater.js). Everything else comes from `mac`.
    mas: {
        appId: identity.storeAppId,
        // Inherited from `mac` otherwise, and the sandbox is what the store wants.
        hardenedRuntime: false,
        entitlements: 'build/entitlements.mas.plist',
        entitlementsInherit: 'build/entitlements.mas.inherit.plist',
        // From the workflow, which writes the profile out of a secret.
        provisioningProfile: process.env.SOLO_MAS_PROVISIONING_PROFILE || null,
        // App Store Connect takes three numbers for the version and wants the
        // build number to grow with every upload of it.
        bundleShortVersion: version.split('-')[0],
        bundleVersion: process.env.SOLO_MAS_BUILD_NUMBER || null,
    },
    // One file per platform, so the arch is redundant in a Windows name.
    nsis: { ...build.nsis, artifactName: `${identity.artifactBase}-\${version}-setup.\${ext}` },
    productName: identity.productName,
    // The installed package, not the build runner's environment, decides its
    // profile and branding every time it starts.
    extraMetadata: { productName: identity.productName },
    // One AppImage, so no arch to disambiguate.
    linux: {
        ...build.linux,
        artifactName: `${identity.artifactBase}-\${version}.\${ext}`,
        ...(identity.preview ? { icon: 'build/icon-preview.png', executableName: identity.executableName } : {}),
    },
    ...(identity.preview ? { win: { ...build.win, icon: 'build/icon-preview.png' } } : {}),
    // `--publish never` in the workflow keeps this from uploading anything;
    // build/updater-feed.mjs writes the feed itself, from the uploaded bytes,
    // because afterAllArtifactBuild re-encodes the .dmg after electron-builder
    // has hashed it. GitHub's asset CDN refuses a multi-range request, which is
    // what a differential download would otherwise try first.
    publish: identity.preview ? null : [{
        provider: 'generic',
        url: UPDATE_FEED,
        channel: 'latest',
        useMultipleRangeRequest: false,
    }],
};
