import { spawn } from 'node:child_process';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

if (process.platform !== 'darwin') {
    console.log('SKIP: the default glass backdrop is macOS-only.');
    process.exit(0);
}

const desktopDir = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const electron = process.env.SOLO_GLASS_APP ?? path.join(desktopDir, 'node_modules', '.bin', 'electron');
const userDataDir = mkdtempSync(path.join(os.tmpdir(), 'solo-glass-check-'));
mkdirSync(userDataDir, { recursive: true });
writeFileSync(path.join(userDataDir, 'settings.json'), JSON.stringify({
    instanceUrl: 'http://127.0.0.1:9',
    port: 42321,
}));

const env = { ...process.env, SOLO_USER_DATA_DIR: userDataDir };
delete env.SOLO_LIQUID_GLASS;
delete env.SOLO_DISABLE_LIQUID_GLASS;
delete env.SOLO_GLASS_APP;

const child = spawn(electron, ['.'], {
    cwd: desktopDir,
    env,
    stdio: ['ignore', 'pipe', 'pipe'],
});

let output = '';
let verdict = null;

function settle(ok, message) {
    if (verdict) return;
    verdict = { ok, message };
    clearTimeout(timer);
    child.kill('SIGINT');
}

function collect(chunk) {
    output += chunk.toString();
    if (/Solo (?:Liquid Glass|glass fallback) enabled/.test(output)) {
        settle(true, 'PASS: glass is active without an opt-in environment variable.');
    }
}

child.stdout.on('data', collect);
child.stderr.on('data', collect);
child.on('error', error => settle(false, `FAIL: Electron could not launch: ${error.message}`));
child.on('close', code => {
    if (!verdict) {
        verdict = { ok: false, message: `FAIL: Electron exited before enabling glass (code ${code}).` };
    }

    rmSync(userDataDir, { recursive: true, force: true });
    if (output.trim()) console.log(output.trim());
    console.log(verdict.message);
    process.exitCode = verdict.ok ? 0 : 1;
});

const timer = setTimeout(() => {
    settle(false, 'FAIL: no glass backdrop was enabled by default.');
}, 5000);
