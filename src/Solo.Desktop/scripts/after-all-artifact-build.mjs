// electron-builder afterAllArtifactBuild hook.
//
// Re-encodes the macOS .dmg as ULMO (lzma). electron-builder's dmg.format only
// offers zlib, bzip2 and lzfse; lzma cuts the image by about a fifth
// against zlib, while bzip2 saves a third as much and mounts several times
// slower. ULMO needs macOS 10.15, below Electron's own floor.
//
// Converting is safe because nothing signs the image itself: dmg.sign defaults
// to false, and notarization covers the .app inside, whose bytes a block-level
// convert carries over untouched. If the .dmg is ever signed, sign it after
// this, not before.
import { execFileSync } from 'node:child_process';
import { renameSync, rmSync } from 'node:fs';

export default async function afterAllArtifactBuild(buildResult) {
    for (const dmg of buildResult.artifactPaths.filter(p => p.endsWith('.dmg'))) {
        const converted = dmg.replace(/\.dmg$/, '.ulmo.dmg');
        rmSync(converted, { force: true });
        execFileSync('hdiutil', ['convert', dmg, '-format', 'ULMO', '-o', converted, '-quiet'], {
            stdio: 'inherit',
        });
        renameSync(converted, dmg);
    }
    // Nothing new to publish; the converted image replaces the original.
    return [];
}
