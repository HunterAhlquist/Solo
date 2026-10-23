import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const require = createRequire(import.meta.url);
const config = require('../electron-builder.config.cjs');
assert.match(config.mac.extendInfo?.NSLocalNetworkUsageDescription ?? '', /Solo.*local network/i,
    'macOS packaging must explain why Solo requests Local Network access.');
assert.equal(config.afterPack, 'scripts/after-pack.mjs', 'Mac packaging must give each build its own executable UUID before signing.');
if (process.platform !== 'darwin') {
    console.log('PASS: macOS local-network permission packaging is configured.');
    process.exit(0);
}
const { default: afterPack } = await import('./after-pack.mjs');
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'solo-mac-network-identity-'));
const electron = path.resolve(new URL('../node_modules/electron/dist/Electron.app/Contents/MacOS/Electron', import.meta.url).pathname);
const uuid = file => execFileSync('dwarfdump', ['--uuid', file], { encoding: 'utf8' }).match(/UUID: ([A-F0-9-]+)/)[1];
const original = uuid(electron);
const seen = new Set([original]);
try {
    for (const productFilename of ['Solo', 'Solo (Preview)', 'Solo (Preview)']) {
        const out = fs.mkdtempSync(path.join(root, 'build-'));
        const executable = path.join(out, productFilename + '.app', 'Contents', 'MacOS', productFilename);
        fs.mkdirSync(path.dirname(executable), { recursive: true });
        fs.copyFileSync(electron, executable);
        fs.writeFileSync(path.join(out, productFilename + '.app', 'Contents', 'Info.plist'),
            `<?xml version="1.0"?><plist version="1.0"><dict><key>CFBundleExecutable</key><string>${productFilename}</string></dict></plist>`);
        const helper = path.join(out, productFilename + '.app', 'Contents', 'Frameworks', 'Fixture Helper.app');
        fs.mkdirSync(path.join(helper, 'Contents', 'MacOS'), { recursive: true });
        fs.writeFileSync(path.join(helper, 'Contents', 'Info.plist'),
            '<?xml version="1.0"?><plist version="1.0"><dict><key>CFBundleExecutable</key><string>Fixture Helper</string></dict></plist>');
        const helperExecutable = path.join(helper, 'Contents', 'MacOS', 'Fixture Helper');
        fs.copyFileSync(electron, helperExecutable);
        const framework = path.join(out, productFilename + '.app', 'Contents', 'Frameworks', 'Electron Framework.framework');
        fs.mkdirSync(framework);
        fs.writeFileSync(path.join(framework, 'Electron Framework'), 'Framework must stay untouched');
        const before = fs.readFileSync(executable);
        await afterPack({ electronPlatformName: 'darwin', appOutDir: out, packager: { appInfo: { productFilename } } });
        const updated = uuid(executable);
        assert.ok(!seen.has(updated), 'Stable, preview and repeated builds must have distinct Mach-O UUIDs.');
        seen.add(updated);
        const helperUuid = uuid(helperExecutable);
        assert.ok(!seen.has(helperUuid), 'Helper launchers must have their own UUIDs too.');
        seen.add(helperUuid);
        assert.equal(fs.readFileSync(path.join(framework, 'Electron Framework'), 'utf8'), 'Framework must stay untouched');
        const after = fs.readFileSync(executable);
        assert.equal(after.length, before.length);
        const differences = before.reduce((count, value, index) => count + (value !== after[index]), 0);
        assert.ok(differences > 0 && differences <= 16, 'Only the LC_UUID payload may change.');
    }
    const bad = fs.mkdtempSync(path.join(root, 'corrupt-'));
    const badContents = path.join(bad, 'Bad.app', 'Contents');
    fs.mkdirSync(path.join(badContents, 'MacOS'), { recursive: true });
    fs.writeFileSync(path.join(badContents, 'Info.plist'),
        '<?xml version="1.0"?><plist version="1.0"><dict><key>CFBundleExecutable</key><string>Bad</string></dict></plist>');
    const malformed = fs.readFileSync(electron);
    malformed.writeUInt32LE(0xffffffff, 36);
    fs.writeFileSync(path.join(badContents, 'MacOS', 'Bad'), malformed);
    await assert.rejects(afterPack({ electronPlatformName: 'darwin', appOutDir: bad,
        packager: { appInfo: { productFilename: 'Bad' } } }), /load command/);
    assert.deepEqual(fs.readFileSync(path.join(badContents, 'MacOS', 'Bad')), malformed);
    assert.equal(uuid(electron), original, 'The shared Electron installation must remain untouched.');
    await afterPack({ electronPlatformName: 'win32' });
    console.log('PASS: actual Electron executables get distinct build UUIDs; only their 16-byte UUID payload changes.');
} finally {
    fs.rmSync(root, { recursive: true, force: true });
}
