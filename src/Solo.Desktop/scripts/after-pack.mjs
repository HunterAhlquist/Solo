import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';

// macOS associates Local Network permission with the launcher UUID (Apple TN3178/TN3179).
// Assign unique UUIDs before signing. Preserve framework UUIDs and their debug symbols.
export default async function afterPack(context) {
    writeUpdateConfig(context);
    if (context.electronPlatformName !== 'darwin') return;
    const app = path.join(context.appOutDir, context.packager.appInfo.productFilename + '.app');
    const frameworks = path.join(app, 'Contents', 'Frameworks');
    const helpers = fs.existsSync(frameworks) ? fs.readdirSync(frameworks)
        .filter(name => name.endsWith('.app')).map(name => path.join(frameworks, name)) : [];
    for (const bundle of [app, ...helpers]) {
        const executableName = execFileSync('/usr/libexec/PlistBuddy', [
            '-c', 'Print :CFBundleExecutable', path.join(bundle, 'Contents', 'Info.plist'),
        ], { encoding: 'utf8' }).trim();
        if (path.basename(executableName) !== executableName) throw new Error('Invalid bundle executable name.');
        const executable = path.join(bundle, 'Contents', 'MacOS', executableName);
        const data = fs.readFileSync(executable);
        if (data.length < 32 || data.readUInt32LE(0) !== 0xfeedfacf) {
            throw new Error(`Expected a thin 64-bit Mach-O launcher: ${executableName}`);
        }
        const count = data.readUInt32LE(16);
        const end = 32 + data.readUInt32LE(20);
        if (end > data.length) throw new Error('Invalid Mach-O load-command bounds.');
        let offset = 32;
        let uuidOffset;
        for (let index = 0; index < count; index++) {
            if (offset + 8 > end) throw new Error('Truncated Mach-O load command.');
            const command = data.readUInt32LE(offset);
            const size = data.readUInt32LE(offset + 4);
            if (size < 8 || offset + size > end) throw new Error('Invalid Mach-O load command.');
            if (command === 0x1b) {
                if (size !== 24 || uuidOffset !== undefined) throw new Error('Invalid Mach-O LC_UUID.');
                uuidOffset = offset + 8;
            }
            offset += size;
        }
        if (uuidOffset === undefined || offset !== end) throw new Error('Launcher has no valid LC_UUID.');
        const uuid = crypto.randomUUID();
        Buffer.from(uuid.replaceAll('-', ''), 'hex').copy(data, uuidOffset);
        fs.writeFileSync(executable, data);
        console.log(`Assigned build UUID ${uuid} to ${executableName} before signing.`);
    }
}

// electron-updater reads app-update.yml out of the packaged app's resources.
// electron-builder writes that file while building a *target*, which this
// repository's mac build never reaches: it packs with `--dir` to stop at the
// signed app (see release-desktop.yml), notarizes and staples it, then wraps
// the finished bundle with `--prepackaged`, which skips packing altogether. The
// result shipped an app with no update config in it and every "Check for
// Updates" failed on the missing file.
//
// Written here because afterPack is the last hook that runs *before* signing.
// Adding a file to Contents/Resources after codesign has sealed the bundle
// breaks the signature, so the `--prepackaged` stage is far too late.
//
// Derived from the resolved publish config rather than restated, so the URL and
// channel cannot drift from electron-builder.config.cjs. A build with no
// publish config writes nothing: that is the preview channel, which has no feed
// on purpose, and main/updater.js reports the absence rather than failing on it.
export function writeUpdateConfig(context) {
    const { packager, appOutDir } = context;

    // Read through, rather than off a known shape. electron-builder's own
    // context always carries this, but afterPack is also driven directly by the
    // checks in this directory with only the fields the hook under test needs —
    // and a hard read here threw a TypeError that broke
    // check-mac-network-identity.mjs, a check the release workflow requires.
    // Nothing is written when there is no publish config either way, so there is
    // no behaviour to lose by reading it defensively.
    const publish = [packager?.info?.config?.publish].flat().filter(Boolean);
    if (publish.length === 0) {
        console.log('No publish configuration, so no app-update.yml: this build cannot self-update.');
        return;
    }

    const resources = context.electronPlatformName === 'darwin'
        ? path.join(appOutDir, packager.appInfo.productFilename + '.app', 'Contents', 'Resources')
        : path.join(appOutDir, 'resources');

    const target = path.join(resources, 'app-update.yml');

    // A full single-platform build does reach the target phase, and
    // electron-builder's own copy is the authority there. Only fill the gap.
    if (fs.existsSync(target)) {
        return;
    }

    const [feed] = publish;
    const config = {
        ...feed,
        // What electron-builder derives for the cache directory. Pinned by
        // scripts/check-updater.mjs against a real build's copy.
        updaterCacheDirName: packager.appInfo.updaterCacheDirName,
    };

    const yaml = Object.entries(config)
        .filter(([, value]) => value !== undefined && value !== null)
        .map(([key, value]) => `${key}: ${value}`)
        .join('\n');

    fs.mkdirSync(resources, { recursive: true });
    fs.writeFileSync(target, yaml + '\n');
    console.log(`Wrote app-update.yml for the ${feed.provider} feed before signing.`);
}
