// `release-manifest.json`, the machine-readable description of a release.
//
// A release train rebuilds only what changed, so a release's own assets are an
// incomplete picture of the product: release N may hold new extension packages
// while the current installers and image still live in release N-2. The
// manifest closes that gap. The newest release's manifest always describes
// every component at its current version, and every entry names the release
// whose assets it points at — so an entry carried forward keeps working.
//
// That makes one file the update feed for all three updaters:
//
//   * the server compares `server.version` with its own and notifies;
//   * the extension host compares `extensions[].version` with what is installed
//     and downloads `extensions[].url`;
//   * the desktop shell reads `desktop.channels`, which are the
//     electron-updater channel files (see build/updater-feed.mjs).
//
// A third-party extension publishes the same shape on its own GitHub release,
// with only an `extensions` entry, so the host reads one format either way.
//
// Both release workflows merge into this file, serialised by a shared
// concurrency group. Whichever runs first seeds the manifest from the previous
// release, so neither has to run for the other to produce a complete one.
//
//   node build/release-manifest.mjs --out release-manifest.json \
//     --tag v1.18.0 --version 1.18.0 --prerelease false \
//     --base https://github.com/owner/repo/releases/download/v1.18.0/ \
//     --baseline previous/release-manifest.json \
//     --packages artifacts --fragment server.json
//
// `--packages` describes every `solo-*.zip` in a directory as an extension
// entry, which is what the release job holds: only the extensions that changed
// were downloaded into it. `--fragment` adds a prepared entry — the server's,
// or the desktop's from build/updater-feed.mjs.
import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import fs from 'node:fs/promises';
import path from 'node:path';

export const SCHEMA = 1;

async function readJson(file) {
    return JSON.parse(await fs.readFile(file, 'utf8'));
}

async function sha512(file) {
    const hash = createHash('sha512');
    for await (const chunk of createReadStream(file)) hash.update(chunk);
    return hash.digest('base64');
}

// Merges a fragment over a baseline. `server`, `desktop` and `contract` are
// whole-component entries: a run that rebuilt one replaces it outright, and one
// that did not leaves the carried-forward entry alone. Extensions merge by id
// for the same reason — a release that rebuilt one extension must not drop the
// other three from the manifest.
export function merge(baseline, fragment) {
    const merged = { ...baseline };
    for (const key of ['server', 'desktop', 'contract']) {
        if (fragment[key] != null) merged[key] = fragment[key];
    }
    if (fragment.extensions != null) {
        const byId = new Map((baseline.extensions ?? []).map(entry => [entry.id, entry]));
        for (const entry of fragment.extensions) byId.set(entry.id, entry);
        merged.extensions = [...byId.values()].sort((a, b) => a.id.localeCompare(b.id));
    }
    return merged;
}

// An extension package is a zip with its extension.json at the root, so the
// manifest entry is read out of the package itself rather than restated in the
// workflow. Keeping the compatibility ranges here lets an updater refuse an
// extension version the running server could not load.
export async function describeExtensions({ dir, tag, base, names }) {
    const entries = [];
    for (const name of names) {
        const file = path.join(dir, name);
        const { size } = await fs.stat(file);
        const manifest = await readZipEntry(file, 'extension.json');
        const { id, version, soloApiVersion, soloClientVersion } = JSON.parse(manifest);
        entries.push({
            id,
            version,
            release: tag,
            asset: name,
            url: new URL(name, base.endsWith('/') ? base : `${base}/`).href,
            size,
            sha512: await sha512(file),
            ...(soloApiVersion ? { soloApiVersion } : {}),
            ...(soloClientVersion ? { soloClientVersion } : {}),
        });
    }
    return entries;
}

// A stored or deflated entry from a zip's central directory. Small enough to do
// here rather than take a dependency on an archive library in the release job.
async function readZipEntry(file, wanted) {
    const buffer = await fs.readFile(file);
    const end = buffer.lastIndexOf(Buffer.from('PK\x05\x06', 'latin1'));
    if (end < 0) throw new Error(`${file} is not a zip.`);
    let offset = buffer.readUInt32LE(end + 16);
    const count = buffer.readUInt16LE(end + 10);
    for (let i = 0; i < count; i++) {
        const method = buffer.readUInt16LE(offset + 10);
        const compressed = buffer.readUInt32LE(offset + 20);
        const nameLength = buffer.readUInt16LE(offset + 28);
        const extraLength = buffer.readUInt16LE(offset + 30);
        const commentLength = buffer.readUInt16LE(offset + 32);
        const localOffset = buffer.readUInt32LE(offset + 42);
        const name = buffer.toString('utf8', offset + 46, offset + 46 + nameLength);
        if (name === wanted) {
            // The local header repeats the name and extra fields, whose lengths
            // can differ from the central directory's.
            const localNameLength = buffer.readUInt16LE(localOffset + 26);
            const localExtraLength = buffer.readUInt16LE(localOffset + 28);
            const start = localOffset + 30 + localNameLength + localExtraLength;
            const data = buffer.subarray(start, start + compressed);
            if (method === 0) return data.toString('utf8');
            const { inflateRawSync } = await import('node:zlib');
            return inflateRawSync(data).toString('utf8');
        }
        offset += 46 + nameLength + extraLength + commentLength;
    }
    throw new Error(`${file} has no ${wanted}.`);
}

async function main(argv) {
    const args = { fragment: [] };
    for (let i = 0; i < argv.length; i += 2) {
        const key = argv[i].replace(/^--/, '');
        if (key === 'fragment') args.fragment.push(argv[i + 1]);
        else args[key] = argv[i + 1];
    }
    for (const required of ['out', 'tag', 'version']) {
        if (!args[required]) throw new Error(`Missing --${required}.`);
    }

    // The baseline is the manifest already on this release if the other
    // workflow got here first, else the previous release's, else nothing. The
    // caller resolves which, and passes an absent path for a first release.
    let manifest = args.baseline
        ? await readJson(args.baseline).catch(() => ({}))
        : {};
    for (const fragment of args.fragment) {
        manifest = merge(manifest, await readJson(fragment));
    }

    if (args.packages) {
        if (!args.base) throw new Error('--packages needs --base to build each download URL.');
        const names = (await fs.readdir(args.packages).catch(() => []))
            .filter(name => name.startsWith('solo-') && name.endsWith('.zip')).sort();
        if (names.length > 0) {
            manifest = merge(manifest, {
                extensions: await describeExtensions({ dir: args.packages, tag: args.tag, base: args.base, names }),
            });
        }
    }

    // Always this release's own, whatever a carried-forward baseline said.
    manifest.schema = SCHEMA;
    manifest.release = {
        tag: args.tag,
        version: args.version,
        prerelease: args.prerelease === 'true',
        published: new Date().toISOString(),
    };

    const ordered = {
        schema: manifest.schema,
        release: manifest.release,
        ...(manifest.server ? { server: manifest.server } : {}),
        ...(manifest.desktop ? { desktop: manifest.desktop } : {}),
        ...(manifest.extensions ? { extensions: manifest.extensions } : {}),
        ...(manifest.contract ? { contract: manifest.contract } : {}),
    };
    await fs.writeFile(args.out, JSON.stringify(ordered, null, 2) + '\n');
    console.log(JSON.stringify(ordered, null, 2));
}

if (process.argv[1] && import.meta.url === new URL(`file://${process.argv[1]}`).href) {
    main(process.argv.slice(2)).catch(error => {
        console.error(`::error::${error.message}`);
        process.exit(1);
    });
}
