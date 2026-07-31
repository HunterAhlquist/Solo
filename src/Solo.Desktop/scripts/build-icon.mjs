// Regenerates every flat icon asset from the Icon Composer source in /Icons.
// Run with `npm run build:icon` after re-exporting Solo.icon.
//
//   src/Solo.Desktop/build/icon.png            1024px, for the win/linux targets
//   src/Solo.Client/wwwroot/apple-touch-icon.png   180px, iOS/Safari home screen
//   src/Solo.Client/wwwroot/favicon.svg        64px light + dark, swapped by media query
//
// macOS is deliberately not in that list. electron-builder compiles the .icon
// with actool and ships the resulting Assets.car in the bundle, so macOS renders
// the Liquid Glass icon live — dynamic appearance, specular highlights, the
// Dock's own masking. Baking a flat .icns for macOS would throw that away.
//
// Requires macOS with Xcode 26+ (actool + swiftc), same as packaging the mac app.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import { compileIcon, reportDropped, stageIcon } from './stage-icon.mjs';

const scriptsDir = path.dirname(fileURLToPath(import.meta.url));
const desktopDir = path.dirname(scriptsDir);
const repoRoot = path.join(desktopDir, '..', '..');

const desktopBuildDir = path.join(desktopDir, 'build');
const clientWwwroot = path.join(repoRoot, 'src', 'Solo.Client', 'wwwroot');

const FAVICON_PX = 64;
const APPLE_TOUCH_PX = 180;
const MASTER_PX = 1024;

// actool reports its diagnostics on stdout, so keep it quiet on success but
// replay it when the command fails — otherwise the reason is swallowed.
const run = (cmd, args) => {
    try {
        return execFileSync(cmd, args, { stdio: ['ignore', 'pipe', 'inherit'] });
    } catch (error) {
        const details = error.stdout?.toString().trim();
        if (details) {
            console.error(details);
        }
        throw new Error(`${cmd} failed`, { cause: error });
    }
};

if (process.platform !== 'darwin') {
    throw new Error('build:icon needs macOS — it renders through actool and AppKit.');
}

const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'solo-icon-'));
try {
    // 1. Compile the .icon the same way electron-builder does when packaging,
    //    from the same staged copy it consumes — so these assets and the mac
    //    app agree on which effects the local actool supports.
    const catalogDir = path.join(tmpDir, 'catalog');
    const stagedIcon = path.join(tmpDir, 'Icon.icon');
    reportDropped(stageIcon(stagedIcon));
    compileIcon(stagedIcon, catalogDir);

    // 2. Wrap the catalog in a throwaway bundle. Both render paths resolve the
    //    icon through a bundle, and IconServices caches per path, so this lives
    //    at a fresh temp path on every run.
    const probeApp = path.join(tmpDir, 'IconProbe.app');
    fs.mkdirSync(path.join(probeApp, 'Contents', 'Resources'), { recursive: true });
    fs.mkdirSync(path.join(probeApp, 'Contents', 'MacOS'), { recursive: true });
    fs.copyFileSync(path.join(catalogDir, 'Assets.car'), path.join(probeApp, 'Contents', 'Resources', 'Assets.car'));
    fs.writeFileSync(path.join(probeApp, 'Contents', 'MacOS', 'IconProbe'), '#!/bin/sh\n', { mode: 0o755 });
    fs.writeFileSync(path.join(probeApp, 'Contents', 'Info.plist'), `<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>com.solo.desktop.iconprobe</string>
<key>CFBundleName</key><string>IconProbe</string>
<key>CFBundleExecutable</key><string>IconProbe</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleIconName</key><string>Icon</string>
<key>LSMinimumSystemVersion</key><string>26.0</string>
</dict></plist>
`);

    // 3. Render. See render-icon.swift for why light and dark take different paths.
    const renderer = path.join(tmpDir, 'render-icon');
    run('swiftc', ['-O', path.join(scriptsDir, 'render-icon.swift'), '-o', renderer]);

    const out = (name) => path.join(tmpDir, name);
    const render = (appearance, framing, specs) =>
        run(renderer, [probeApp, appearance, framing, ...specs.map(([px, name]) => `${px}:${out(name)}`)]);

    // The win/linux master keeps the macOS canvas — neither platform adds a
    // shadow of its own, and a margin is unremarkable there. The web icons are
    // cropped to the shape: see render-icon.swift.
    render('light', 'fit', [[MASTER_PX, 'light-master.png']]);
    render('light', 'fill', [
        [APPLE_TOUCH_PX, 'light-apple-touch.png'],
        [FAVICON_PX, 'light-favicon.png'],
    ]);
    render('dark', 'fill', [[FAVICON_PX, 'dark-favicon.png']]);

    // IconServices bakes in the machine's appearance and ignores any override,
    // so in Light Mode the "dark" render silently comes back light.
    const lightFavicon = fs.readFileSync(out('light-favicon.png'));
    const darkFavicon = fs.readFileSync(out('dark-favicon.png'));
    if (lightFavicon.equals(darkFavicon)) {
        throw new Error(
            'The dark render came back identical to the light one.\n' +
            'macOS renders it through IconServices, which uses the system appearance:\n' +
            'switch System Settings > Appearance to Dark and run this again.');
    }

    // 4. Write the assets.
    fs.mkdirSync(desktopBuildDir, { recursive: true });
    fs.copyFileSync(out('light-master.png'), path.join(desktopBuildDir, 'icon.png'));
    fs.copyFileSync(out('light-apple-touch.png'), path.join(clientWwwroot, 'apple-touch-icon.png'));

    const faviconPath = path.join(clientWwwroot, 'favicon.svg');
    fs.writeFileSync(faviconPath, `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${FAVICON_PX} ${FAVICON_PX}">
  <!-- Generated by src/Solo.Desktop/scripts/build-icon.mjs from Icons/Solo.icon; do not edit. -->
  <style>.dark{display:none}@media (prefers-color-scheme:dark){.light{display:none}.dark{display:inline}}</style>
  <image class="light" width="${FAVICON_PX}" height="${FAVICON_PX}" href="data:image/png;base64,${lightFavicon.toString('base64')}"/>
  <image class="dark" width="${FAVICON_PX}" height="${FAVICON_PX}" href="data:image/png;base64,${darkFavicon.toString('base64')}"/>
</svg>
`);

    console.log([
        path.join(desktopBuildDir, 'icon.png'),
        path.join(clientWwwroot, 'apple-touch-icon.png'),
        faviconPath,
    ].map((p) => `Wrote ${path.relative(repoRoot, p)}`).join('\n'));
} finally {
    fs.rmSync(tmpDir, { recursive: true, force: true });
}
