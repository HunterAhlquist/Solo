// Builds the desktop app's icons from the Icon Composer export in /Icons:
//   build/icon.icns — complete macOS iconset (16→1024, including @2x reps),
//   build/icon.png  — 1024px master, for future win/linux targets.
// The source is the iOS "Default" appearance export (full-canvas squircle,
// pre-masked corners, baked shadow) — macOS Tahoe re-masks Dock icons to the
// same shape, so it lands pixel-fit. Electron's nativeImage does the resizing
// (electron-builder's own png→icns conversion mangles the small reps).
// Run via `npm run build:icon` after re-exporting from Icon Composer.
import { app, nativeImage } from 'electron';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';

const desktopDir = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const srcPath = path.join(desktopDir, '..', '..', 'Icons', 'Solo-iOS-Default-1024@1x.png');
const buildDir = path.join(desktopDir, 'build');

const CANVAS = 1024;

// iconutil's required naming: icon_<pt>x<pt>[@2x].png
const ICONSET = [
    ['icon_16x16.png', 16], ['icon_16x16@2x.png', 32],
    ['icon_32x32.png', 32], ['icon_32x32@2x.png', 64],
    ['icon_128x128.png', 128], ['icon_128x128@2x.png', 256],
    ['icon_256x256.png', 256], ['icon_256x256@2x.png', 512],
    ['icon_512x512.png', 512], ['icon_512x512@2x.png', 1024],
];

app.whenReady().then(() => {
    const master = nativeImage.createFromPath(srcPath);
    const { width } = master.getSize();
    if (width !== CANVAS) {
        throw new Error(`Expected ${CANVAS}px master at ${srcPath}, got ${width}px`);
    }

    fs.mkdirSync(buildDir, { recursive: true });
    fs.writeFileSync(path.join(buildDir, 'icon.png'), master.toPNG());

    const iconsetDir = path.join(buildDir, 'icon.iconset');
    fs.rmSync(iconsetDir, { recursive: true, force: true });
    fs.mkdirSync(iconsetDir);
    for (const [name, size] of ICONSET) {
        const image = size === CANVAS ? master : master.resize({ width: size, height: size });
        fs.writeFileSync(path.join(iconsetDir, name), image.toPNG());
    }

    execFileSync('iconutil', ['-c', 'icns', iconsetDir, '-o', path.join(buildDir, 'icon.icns')]);
    fs.rmSync(iconsetDir, { recursive: true, force: true });

    console.log(`Wrote ${path.join(buildDir, 'icon.icns')} and icon.png`);
    app.exit(0);
});
