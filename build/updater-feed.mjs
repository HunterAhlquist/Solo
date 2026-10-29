// The electron-updater channel files for a release, plus the desktop half of
// its release manifest.
//
// Run from the release job, over the installers already downloaded from the
// build matrix:
//
//   node build/updater-feed.mjs --dist artifacts --out feed \
//     --version 1.18.0 --tag v1.18.0 \
//     --base https://github.com/owner/repo/releases/download/v1.18.0/
//
// Why this exists rather than electron-builder's own feed:
//
//   * The URLs have to be absolute. electron-updater's GitHub provider rebuilds
//     every download URL from the release tag it found, so a feed served from
//     release N can only point at assets in release N. This release train
//     rebuilds the desktop only when the desktop changed, so release N often
//     has to point at installers that live in an earlier release — which only
//     the `generic` provider can do, and only from absolute URLs (electron-
//     updater resolves a file's `url` with `new URL(url, base)`, so an absolute
//     one replaces the base).
//   * The macOS .dmg is re-encoded as ULMO after electron-builder has finished
//     with it (scripts/after-all-artifact-build.mjs), so any digest taken
//     during packaging is stale. These are taken from the uploaded bytes.
//
// A .dmg cannot be an update payload — Squirrel.Mac takes a zip — so the mac
// channel is refused unless a zip is present beside the image.
import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import fs from 'node:fs/promises';
import path from 'node:path';

// Each platform's channel file, the extensions that belong in it, and the one
// electron-updater actually downloads. `primary` is repeated at the top level
// of the file as the legacy single-file form, which older clients still read.
const CHANNELS = [
    { file: 'latest-mac.yml', platform: 'mac', include: ['.dmg', '.zip'], primary: '.zip' },
    { file: 'latest.yml', platform: 'win', include: ['.exe'], primary: '.exe' },
    { file: 'latest-linux.yml', platform: 'linux', include: ['.AppImage'], primary: '.AppImage' },
];

export async function sha512(file) {
    const hash = createHash('sha512');
    for await (const chunk of createReadStream(file)) hash.update(chunk);
    return hash.digest('base64');
}

// YAML, by hand, for the handful of scalars this file holds. Everything written
// here is a plain scalar by construction — a SemVer version, a filename with no
// spaces, an https URL, an integer, or base64, whose alphabet holds no YAML
// indicator. The date is quoted because electron-builder quotes it, and an
// unquoted one would parse as a timestamp rather than a string.
function emit(info) {
    const lines = [`version: ${info.version}`, 'files:'];
    for (const file of info.files) {
        lines.push(`  - url: ${file.url}`, `    sha512: ${file.sha512}`, `    size: ${file.size}`);
        if (file.blockMapSize != null) lines.push(`    blockMapSize: ${file.blockMapSize}`);
    }
    lines.push(`path: ${info.path}`, `sha512: ${info.sha512}`, `releaseDate: '${info.releaseDate}'`);
    return lines.join('\n') + '\n';
}

export async function buildFeed({ dist, version, tag, base, releaseDate = new Date().toISOString() }) {
    // A trailing slash makes the base a directory for `new URL`; without one its
    // last segment would be replaced rather than appended to.
    const baseUrl = base.endsWith('/') ? base : `${base}/`;
    const names = await fs.readdir(dist);
    const channels = [];
    const installers = [];

    for (const channel of CHANNELS) {
        const matched = names.filter(name => channel.include.some(ext => name.endsWith(ext))).sort();
        if (matched.length === 0) continue;

        const files = [];
        for (const name of matched) {
            // GitHub rewrites a space in an asset name to a dot, which would
            // leave every URL in this file pointing at something that does not
            // exist. electron-builder.config.cjs names the artifacts to avoid
            // it; this is the check that it worked.
            if (/\s/.test(name)) throw new Error(`Installer name has a space, which GitHub will rewrite: ${name}`);
            const file = path.join(dist, name);
            const { size } = await fs.stat(file);
            // Differential download metadata, when the target produced it.
            // electron-updater falls back to a full download without it, so its
            // absence is not an error.
            const blockMap = await fs.stat(`${file}.blockmap`).catch(() => null);
            files.push({
                url: new URL(name, baseUrl).href,
                sha512: await sha512(file),
                size,
                ...(blockMap ? { blockMapSize: blockMap.size } : {}),
                name,
                platform: channel.platform,
            });
        }

        const primary = files.find(file => file.name.endsWith(channel.primary));
        if (!primary) {
            throw new Error(`${channel.file} needs a ${channel.primary}: electron-updater cannot install from `
                + `${matched.join(', ')} alone.`);
        }

        channels.push({
            file: channel.file,
            contents: emit({
                version,
                files: files.map(({ name, platform, ...rest }) => rest),
                path: primary.name,
                sha512: primary.sha512,
                releaseDate,
            }),
        });
        installers.push(...files.map(({ name, platform, url, size, sha512: digest }) =>
            ({ platform, asset: name, url, size, sha512: digest, primary: name === primary.name })));
    }

    if (channels.length === 0) throw new Error(`No installers found in ${dist}.`);

    return {
        channels,
        // The desktop half of the release manifest, merged in by
        // build/release-manifest.mjs. `release` is the tag whose assets the URLs
        // above point at, which stays correct once a later release carries this
        // entry forward without rebuilding.
        manifest: {
            desktop: {
                version,
                release: tag,
                channels: Object.fromEntries(channels.map(c => [
                    CHANNELS.find(k => k.file === c.file).platform, new URL(c.file, baseUrl).href,
                ])),
                installers,
            },
        },
    };
}

async function main(argv) {
    const args = {};
    for (let i = 0; i < argv.length; i += 2) args[argv[i].replace(/^--/, '')] = argv[i + 1];
    for (const required of ['dist', 'out', 'version', 'tag', 'base']) {
        if (!args[required]) throw new Error(`Missing --${required}.`);
    }

    const { channels, manifest } = await buildFeed(args);
    await fs.mkdir(args.out, { recursive: true });
    for (const channel of channels) {
        await fs.writeFile(path.join(args.out, channel.file), channel.contents);
        console.log(`Wrote ${channel.file}`);
        console.log(channel.contents.replace(/^/gm, '  '));
    }
    await fs.writeFile(path.join(args.out, 'desktop-manifest.json'), JSON.stringify(manifest, null, 2) + '\n');
    console.log(`Wrote desktop-manifest.json`);
}

if (process.argv[1] && import.meta.url === new URL(`file://${process.argv[1]}`).href) {
    main(process.argv.slice(2)).catch(error => {
        console.error(`::error::${error.message}`);
        process.exit(1);
    });
}
