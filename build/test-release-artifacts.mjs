// The release train's two assembly steps: the electron-updater channel files
// and the merged release manifest. Both decide what an installed app downloads,
// and neither is exercised until a tag is pushed, so they are checked here.
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

const root = path.resolve(import.meta.dirname, '..');
const { buildFeed } = await import(path.join(root, 'build/updater-feed.mjs'));
const { merge, describeExtensions } = await import(path.join(root, 'build/release-manifest.mjs'));

const BASE = 'https://github.com/owner/repo/releases/download/v1.18.0/';

async function scratch() {
    const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'solo-release-'));
    test.after(() => fs.rm(dir, { recursive: true, force: true }));
    return dir;
}

async function installers(dir, files) {
    for (const [name, contents] of Object.entries(files)) {
        await fs.writeFile(path.join(dir, name), contents);
    }
}

test('the feed links absolute URLs and offers the mac zip, not the image', async () => {
    const dist = await scratch();
    await installers(dist, {
        'Solo-1.18.0-arm64.dmg': 'image',
        'Solo-1.18.0-arm64.zip': 'payload',
        'Solo-1.18.0-setup.exe': 'setup',
        'Solo-1.18.0-x64.AppImage': 'appimage',
        'Solo-1.18.0-setup.exe.blockmap': '0123456789',
    });

    const { channels, manifest } = await buildFeed({
        dist, version: '1.18.0', tag: 'v1.18.0', base: BASE, releaseDate: '2026-10-29T00:00:00.000Z',
    });
    const byFile = Object.fromEntries(channels.map(c => [c.file, c.contents]));

    assert.deepEqual(Object.keys(byFile).sort(), ['latest-linux.yml', 'latest-mac.yml', 'latest.yml']);

    // Squirrel.Mac installs from the zip; the image is listed but never the
    // one electron-updater reaches for.
    assert.match(byFile['latest-mac.yml'], /^path: Solo-1\.18\.0-arm64\.zip$/m);
    const expectedZip = createHash('sha512').update('payload').digest('base64');
    assert.match(byFile['latest-mac.yml'], new RegExp(`^sha512: ${expectedZip.replace(/[+/=]/g, '\\$&')}$`, 'm'));

    // Absolute, so a later release's feed can still point here.
    assert.match(byFile['latest.yml'], /^ {2}- url: https:\/\/github\.com\/owner\/repo\/releases\/download\/v1\.18\.0\/Solo-1\.18\.0-setup\.exe$/m);
    assert.match(byFile['latest.yml'], /^ {4}blockMapSize: 10$/m);
    // No blockmap beside the AppImage, so no blockMapSize claimed for it.
    assert.doesNotMatch(byFile['latest-linux.yml'], /blockMapSize/);

    assert.match(byFile['latest-mac.yml'], /^releaseDate: '2026-10-29T00:00:00\.000Z'$/m);
    assert.equal(manifest.desktop.release, 'v1.18.0');
    assert.equal(manifest.desktop.channels.mac, `${BASE}latest-mac.yml`);
    assert.equal(manifest.desktop.installers.filter(i => i.primary).length, 3);
});

test('a mac image without a zip is refused rather than shipped unusable', async () => {
    const dist = await scratch();
    await installers(dist, { 'Solo-1.18.0-arm64.dmg': 'image' });
    await assert.rejects(
        buildFeed({ dist, version: '1.18.0', tag: 'v1.18.0', base: BASE }),
        /needs a \.zip/,
    );
});

test('a space in an installer name is refused: GitHub would rewrite it', async () => {
    const dist = await scratch();
    await installers(dist, { 'Solo Setup 1.18.0.exe': 'setup' });
    await assert.rejects(
        buildFeed({ dist, version: '1.18.0', tag: 'v1.18.0', base: BASE }),
        /GitHub will rewrite/,
    );
});

test('merging carries unchanged components forward and replaces rebuilt ones', () => {
    const baseline = {
        server: { version: '1.16.8', release: 'v1.16.0' },
        desktop: { version: '1.17.2', release: 'v1.17.0' },
        extensions: [
            { id: 'mcp', version: '1.6.1', release: 'v1.16.0' },
            { id: 'roadmap', version: '1.8.0', release: 'v1.16.0' },
        ],
    };

    // A release that rebuilt the server and one extension.
    const merged = merge(baseline, {
        server: { version: '1.18.0', release: 'v1.18.0' },
        extensions: [{ id: 'roadmap', version: '1.9.0', release: 'v1.18.0' }],
    });

    assert.deepEqual(merged.server, { version: '1.18.0', release: 'v1.18.0' });
    // Untouched: the installers still live in v1.17.0 and must stay reachable.
    assert.deepEqual(merged.desktop, { version: '1.17.2', release: 'v1.17.0' });
    assert.deepEqual(merged.extensions, [
        { id: 'mcp', version: '1.6.1', release: 'v1.16.0' },
        { id: 'roadmap', version: '1.9.0', release: 'v1.18.0' },
    ]);
});

test('an extension entry is read out of the package it describes', async () => {
    const dir = await scratch();
    const staging = path.join(dir, 'staging');
    await fs.mkdir(staging);
    await fs.writeFile(path.join(staging, 'extension.json'), JSON.stringify({
        id: 'design-docs', version: '1.2.1', soloApiVersion: '>=1.10 <2.0', soloClientVersion: '>=1.11 <2.0',
    }));
    await fs.writeFile(path.join(staging, 'filler.txt'), 'x'.repeat(4096));
    execFileSync('zip', ['-qrX', path.join(dir, 'solo-design-docs-1.2.1.zip'), '.'], { cwd: staging });

    const [entry] = await describeExtensions({
        dir, tag: 'v1.18.0', base: BASE, names: ['solo-design-docs-1.2.1.zip'],
    });
    assert.equal(entry.id, 'design-docs');
    assert.equal(entry.version, '1.2.1');
    assert.equal(entry.release, 'v1.18.0');
    assert.equal(entry.url, `${BASE}solo-design-docs-1.2.1.zip`);
    assert.equal(entry.soloApiVersion, '>=1.10 <2.0');
    assert.ok(entry.size > 0);
    assert.match(entry.sha512, /^[A-Za-z0-9+/]+=*$/);
});
