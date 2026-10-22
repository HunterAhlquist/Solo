// Builds native/auth-session, the macOS sign-in sheet (main/auth-sheet.js).
// Anywhere else there is nothing to build: sign-in uses the default browser.
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

if (process.platform !== 'darwin') {
    console.log('Skipping the sign-in sheet: macOS only.');
    process.exit(0);
}

// Apple silicon, whatever the build machine: the mac release is arm64 only
// (package.json's mac target), and an add-on of another architecture fails to
// load, which quietly drops sign-in back to the browser.
const directory = fileURLToPath(new URL('../native/auth-session', import.meta.url));
const result = spawnSync('npx', ['--no-install', 'node-gyp', 'rebuild', '--arch=arm64'],
    { cwd: directory, stdio: 'inherit' });
process.exit(result.status ?? 1);
