// Stages Icons/Solo.icon into a build copy that the installed actool can
// actually compile, and reports anything it had to drop.
//
// Icon Composer records the newer effects an icon uses in a top-level
// "features" list, so an older renderer can fall back to drawing without them.
// actool doesn't degrade that way: given a feature name it doesn't know it
// crashes outright ("attempt to insert nil object"), and it does this for any
// unrecognised name, not just real ones. An empty list, or a completely
// unknown top-level key, compiles fine — it's specifically the feature-name
// lookup that blows up.
//
// So the fallback gets done here instead: each declared feature is probed
// against the local actool, unsupported ones are dropped from the staged copy,
// and the source in Icons/ is left exactly as Icon Composer saved it. Install
// a newer Xcode and the probe starts passing — nothing here needs changing.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';

const scriptsDir = path.dirname(fileURLToPath(import.meta.url));
const desktopDir = path.dirname(scriptsDir);
const repoRoot = path.join(desktopDir, '..', '..');

export const ICON_SOURCE = path.join(repoRoot, 'Icons', 'Solo.icon');
export const STAGED_ICON = path.join(desktopDir, 'build', 'Solo.icon');

// The catalog names the asset after the bundle's filename and --app-icon looks
// it up by that name, so anything compiling a .icon has to stage it as
// Icon.icon first. electron-builder does the same thing internally.
export const compileIcon = (iconDir, outputDir) => {
    fs.mkdirSync(outputDir, { recursive: true });
    execFileSync('actool', [
        iconDir,
        '--compile', outputDir,
        '--output-format', 'human-readable-text',
        '--output-partial-info-plist', path.join(outputDir, 'partial.plist'),
        '--app-icon', 'Icon',
        '--include-all-app-icons',
        '--enable-on-demand-resources', 'NO',
        '--development-region', 'en',
        '--target-device', 'mac',
        '--minimum-deployment-target', '26.0',
        '--platform', 'macosx',
    ], { stdio: ['ignore', 'pipe', 'pipe'] });
};

const compiles = (iconDir, features) => {
    const probeDir = fs.mkdtempSync(path.join(os.tmpdir(), 'solo-icon-probe-'));
    try {
        const staged = path.join(probeDir, 'Icon.icon');
        fs.cpSync(iconDir, staged, { recursive: true });
        writeFeatures(staged, features);
        compileIcon(staged, path.join(probeDir, 'out'));
        return fs.existsSync(path.join(probeDir, 'out', 'Assets.car'));
    } catch {
        return false;
    } finally {
        fs.rmSync(probeDir, { recursive: true, force: true });
    }
};

const manifestPath = (iconDir) => path.join(iconDir, 'icon.json');

const writeFeatures = (iconDir, features) => {
    const manifest = JSON.parse(fs.readFileSync(manifestPath(iconDir), 'utf8'));
    if (features.length > 0) {
        manifest.features = features;
    } else {
        delete manifest.features;
    }
    fs.writeFileSync(manifestPath(iconDir), `${JSON.stringify(manifest, null, 2)}\n`);
};

/**
 * Copies ICON_SOURCE to `dest`, keeping only the features the local actool
 * understands. Returns the features that had to be dropped.
 */
export const stageIcon = (dest = STAGED_ICON) => {
    if (process.platform !== 'darwin') {
        throw new Error('Staging the icon needs macOS — it compiles through actool.');
    }

    fs.rmSync(dest, { recursive: true, force: true });
    fs.mkdirSync(path.dirname(dest), { recursive: true });
    fs.cpSync(ICON_SOURCE, dest, { recursive: true });

    const declared = JSON.parse(fs.readFileSync(manifestPath(dest), 'utf8')).features ?? [];
    if (declared.length === 0 || compiles(dest, declared)) {
        return [];
    }

    const supported = declared.filter((feature) => compiles(dest, [feature]));
    const dropped = declared.filter((feature) => !supported.includes(feature));
    writeFeatures(dest, supported);

    if (!compiles(dest, supported)) {
        throw new Error(
            `actool cannot compile ${path.relative(repoRoot, ICON_SOURCE)} even with ` +
            `every unsupported feature dropped (${declared.join(', ')}).`);
    }
    return dropped;
};

export const reportDropped = (dropped) => {
    if (dropped.length === 0) {
        return;
    }
    const version = execFileSync('actool', ['--version'], { encoding: 'utf8' })
        .match(/<key>short-bundle-version<\/key>\s*<string>([^<]+)<\/string>/)?.[1] ?? 'unknown';
    const summary = `dropped icon feature(s) [${dropped.join(', ')}] — actool ${version} doesn't support them`;

    // A tag push is a release build. Everyone who installs it gets the degraded
    // icon and nothing says so, which is worse than the run failing — the fix
    // is a runner image with a newer Xcode, not a rebuild.
    if (process.env.GITHUB_REF_TYPE === 'tag') {
        throw new Error(
            `${summary}.\nRefusing to package a release with a degraded icon: ` +
            `build this on a runner image whose Xcode supports them.`);
    }

    // Anywhere else the fallback is fine, but a plain stdout line scrolls off
    // the end of a CI log unread; an annotation lands on the run summary.
    if (process.env.GITHUB_ACTIONS === 'true') {
        console.warn(
            `::warning title=Icon feature dropped::${summary}. The icon renders ` +
            `without them; build on a newer Xcode to get them back.`);
        return;
    }

    console.warn(
        `\nWarning: ${summary}.\nThe icon builds and renders without them; install a newer ` +
        `Xcode and rebuild to get them back.\nIcons/Solo.icon itself is untouched.\n`);
};

// Run directly (npm run stage:icon) as the prebuild step for electron-builder,
// whose mac.icon points at the staged copy.
if (process.argv[1] === fileURLToPath(import.meta.url)) {
    const dropped = stageIcon();
    reportDropped(dropped);
    console.log(`Staged ${path.relative(repoRoot, STAGED_ICON)}`);
}
