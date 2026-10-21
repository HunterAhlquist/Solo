// Renders the installer artwork from the HTML pages in scripts/installer-art.
// Run with `npm run build:installer-art` after changing those pages or the
// icon (it reads build/icon.png, so run `npm run build:icon` first).
//
//   build/background.png, background@2x.png   the macOS disk image's Finder window;
//                                              electron-builder joins the pair into a
//                                              Retina .tiff and sizes the window to it
//   build/installerSidebar.bmp                 the Windows installer's Finish page and
//                                              the uninstaller's Welcome page
//   build/installerHeader.bmp                  the Windows installer's page header
//
// Electron renders them so the art uses the same engine, fonts and CSS as the
// app it introduces, and so a change is an edit to a page rather than to a
// bitmap. The outputs are committed, like build/icon.png: CI packages from
// them and never renders.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { app, BrowserWindow } from 'electron';

const scriptsDir = path.dirname(fileURLToPath(import.meta.url));
const desktopDir = path.dirname(scriptsDir);
const artDir = path.join(scriptsDir, 'installer-art');
const buildDir = path.join(desktopDir, 'build');

// Sizes are fixed by the consumers, not chosen: NSIS's MUI2 pages are laid out
// for exactly 164 × 314 and 150 × 57, and the disk image's window is whatever
// size its background is.
const jobs = [
    { page: 'dmg.html', width: 660, height: 400, scale: 1, out: 'background.png' },
    { page: 'dmg.html', width: 660, height: 400, scale: 2, out: 'background@2x.png' },
    { page: 'sidebar.html', width: 164, height: 314, scale: 1, out: 'installerSidebar.bmp' },
    { page: 'header.html', width: 150, height: 57, scale: 1, out: 'installerHeader.bmp' },
];

async function render({ page, width, height, scale }) {
    const win = new BrowserWindow({
        show: false,
        width: width * scale,
        height: height * scale,
        useContentSize: true,
        webPreferences: { offscreen: true },
    });
    try {
        await win.loadFile(path.join(artDir, page));
        win.webContents.setZoomFactor(scale);
        // Fonts and the icon load asynchronously; capture only once both have.
        await win.webContents.executeJavaScript(
            'document.fonts.ready.then(() => Promise.all([...document.images].map(i => i.decode())))');
        // A frame for the zoom to lay out and paint.
        await new Promise((resolve) => setTimeout(resolve, 250));
        const image = await win.webContents.capturePage();
        const size = image.getSize();
        if (size.width !== width * scale || size.height !== height * scale) {
            throw new Error(`${page} rendered at ${size.width}×${size.height}, expected ${width * scale}×${height * scale}`);
        }
        return image;
    } finally {
        win.destroy();
    }
}

// NSIS loads its bitmaps with LoadImage, which takes BMP and nothing else.
// 24-bit, bottom-up, rows padded to four bytes; the art is opaque, so the
// alpha channel is simply dropped.
function toBmp(image) {
    const { width, height } = image.getSize();
    const bgra = image.toBitmap();
    const stride = Math.ceil((width * 3) / 4) * 4;
    const pixelBytes = stride * height;
    const bmp = Buffer.alloc(54 + pixelBytes);

    bmp.write('BM', 0, 'ascii');
    bmp.writeUInt32LE(bmp.length, 2);
    bmp.writeUInt32LE(54, 10);              // offset to the pixels
    bmp.writeUInt32LE(40, 14);              // BITMAPINFOHEADER
    bmp.writeInt32LE(width, 18);
    bmp.writeInt32LE(height, 22);           // positive: bottom-up
    bmp.writeUInt16LE(1, 26);               // planes
    bmp.writeUInt16LE(24, 28);              // bits per pixel
    bmp.writeUInt32LE(pixelBytes, 34);
    bmp.writeInt32LE(2835, 38);             // 72 dpi
    bmp.writeInt32LE(2835, 42);

    for (let y = 0; y < height; y++) {
        const row = 54 + (height - 1 - y) * stride;
        for (let x = 0; x < width; x++) {
            const src = (y * width + x) * 4;
            bgra.copy(bmp, row + x * 3, src, src + 3);
        }
    }
    return bmp;
}

async function main() {
    try {
        for (const job of jobs) {
            const image = await render(job);
            const target = path.join(buildDir, job.out);
            fs.writeFileSync(target, job.out.endsWith('.bmp') ? toBmp(image) : image.toPNG());
            console.log(`Wrote ${path.relative(desktopDir, target)}`);
        }
    } catch (error) {
        console.error(error);
        process.exitCode = 1;
    } finally {
        app.quit();
    }
}

app.disableHardwareAcceleration();
// Each render closes its window, and by default Electron quits when the last
// one goes — before the next render has opened its own.
app.on('window-all-closed', () => {});
// Not a top-level await: Electron fires `ready` only once an ES module main
// script has finished evaluating, so awaiting it here would never return.
app.whenReady().then(main);
