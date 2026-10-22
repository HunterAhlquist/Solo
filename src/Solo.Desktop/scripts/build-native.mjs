// Builds native/auth-session, the macOS sign-in sheet (main/auth-sheet.js).
// Anywhere else there is nothing to build: sign-in uses the default browser.
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

if (process.platform !== 'darwin') {
    console.log('Skipping the sign-in sheet: macOS only.');
    process.exit(0);
}

const directory = fileURLToPath(new URL('../native/auth-session', import.meta.url));
const result = spawnSync('npx', ['--no-install', 'node-gyp', 'rebuild'], { cwd: directory, stdio: 'inherit' });
process.exit(result.status ?? 1);
