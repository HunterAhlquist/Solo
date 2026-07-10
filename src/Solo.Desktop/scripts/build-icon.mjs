// Renders the client's favicon.svg into the desktop app's icons:
//   build/icon.icns — complete macOS iconset (16→1024, including @2x reps),
//   build/icon.png  — 1024px master, for future win/linux targets.
// Electron is the SVG rasterizer — it's already in node_modules and renders
// exactly what the app shows — and its native image resizer produces the
// small representations (electron-builder's own png→icns conversion mangles
// them). Run via `npm run build:icon` after changing the favicon.
//
// Layout follows Apple's icon grid: the artwork occupies a centered 824px
// square on a transparent 1024px canvas; the SVG's own rounded-rect (rx 7/32
// ≈ 22%) lands right on the macOS squircle ratio.
import { app, BrowserWindow, nativeImage } from 'electron';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';

const desktopDir = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const svgPath = path.join(desktopDir, '..', 'Solo.Client', 'wwwroot', 'favicon.svg');
const buildDir = path.join(desktopDir, 'build');

const CANVAS = 1024;
const ART = 824;

// iconutil's required naming: icon_<pt>x<pt>[@2x].png
const ICONSET = [
    ['icon_16x16.png', 16], ['icon_16x16@2x.png', 32],
    ['icon_32x32.png', 32], ['icon_32x32@2x.png', 64],
    ['icon_128x128.png', 128], ['icon_128x128@2x.png', 256],
    ['icon_256x256.png', 256], ['icon_256x256@2x.png', 512],
    ['icon_512x512.png', 512], ['icon_512x512@2x.png', 1024],
];

const svg = encodeURIComponent(fs.readFileSync(svgPath, 'utf8'));
const html = `<!doctype html><body style="margin:0;width:${CANVAS}px;height:${CANVAS}px;
    background:transparent;display:grid;place-content:center">
    <img src="data:image/svg+xml,${svg}" style="width:${ART}px;height:${ART}px"></body>`;

app.whenReady().then(async () => {
    const win = new BrowserWindow({
        width: CANVAS,
        height: CANVAS,
        useContentSize: true,
        show: false,
        frame: false,
        transparent: true,
        webPreferences: { offscreen: true },
    });

    await win.loadURL('data:text/html;charset=utf-8,' + encodeURIComponent(html));
    // One paint beyond load so the <img> has decoded before capture.
    await new Promise(resolve => setTimeout(resolve, 300));

    let master = await win.webContents.capturePage();
    // A HiDPI display can capture at 2×; normalize to exactly 1024.
    if (master.getSize().width !== CANVAS) {
        master = master.resize({ width: CANVAS, height: CANVAS });
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
