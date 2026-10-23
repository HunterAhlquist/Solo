import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import vm from 'node:vm';
import { createRequire } from 'node:module';
import { EventEmitter } from 'node:events';

const require = createRequire(import.meta.url);
const mainFile = require.resolve('../main.js');
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'solo-identity-check-'));
try {
    for (const [version, preview] of [['1.17.0', false], ['1.17.0-design-docs-codex.9', true]]) {
        for (const override of [false, true]) {
            const paths = { appData: root, userData: path.join(root, 'Solo'), sessionData: path.join(root, 'Solo') };
            let name = 'Solo';
            let locked = false;
            const app = Object.assign(new EventEmitter(), {
                isPackaged: true,
                getVersion: () => version,
                getName: () => name,
                setName: value => { name = value; },
                getPath: key => paths[key],
                setPath: (key, value) => { assert.equal(locked, false, 'Identity changed after the process lock'); paths[key] = value; },
                setAppLogsPath: value => { paths.logs = value; },
                userAgentFallback: 'Mozilla/5.0 Solo/1.17.0 Electron/44.7.0',
                requestSingleInstanceLock: () => { locked = true; return true; },
                whenReady: () => new Promise(() => {}),
            });
            const env = override ? { SOLO_USER_DATA_DIR: path.join(root, 'automation') } : {};
            vm.runInNewContext(fs.readFileSync(mainFile, 'utf8'), {
                require: id => id === 'electron' ? { app } : id.startsWith('./main/') && id !== './main/app-identity'
                    ? {} : require(require.resolve(id, { paths: [path.dirname(mainFile)] })),
                process: { env, platform: process.platform, on() {} },
                __dirname: path.dirname(mainFile),
                console,
            }, { filename: mainFile });
            const expected = env.SOLO_USER_DATA_DIR ?? path.join(root, preview ? 'Solo (Preview)' : 'Solo');
            assert.equal(paths.userData, expected, `${version}: settings directory`);
            assert.equal(paths.sessionData, expected, `${version}: cache and browser storage directory`);
            assert.equal(name, preview ? 'Solo (Preview)' : 'Solo');
            if (preview || override) {
                assert.ok(paths.logs.startsWith(expected + path.sep));
                assert.ok(paths.crashDumps.startsWith(expected + path.sep));
            }
        }
    }
    console.log('PASS: stable and preview isolate settings, browser storage, logs and crash dumps before acquiring the app lock; automation overrides isolate both.');
} finally {
    fs.rmSync(root, { recursive: true, force: true });
}

const pkg = require('../package.json');
for (const [version, preview] of [['1.17.0', false], ['1.17.0-design-docs-codex.9', true]]) {
    const scope = { module: { exports: {} }, require: id => id === './package.json'
        ? { ...pkg, version } : require('../main/app-identity') };
    vm.runInNewContext(fs.readFileSync(require.resolve('../electron-builder.config.cjs'), 'utf8'), scope);
    const config = scope.module.exports;
    assert.equal(config.productName, preview ? 'Solo (Preview)' : 'Solo');
    assert.equal(config.extraMetadata.productName, config.productName);
    assert.equal(config.appId, preview ? 'com.solo.desktop.preview' : 'com.solo.desktop');
    if (preview) {
        assert.equal(config.linux.executableName, 'solo-preview');
        assert.equal(config.win.icon, 'build/icon-preview.png');
        assert.equal(config.linux.icon, 'build/icon-preview.png');
    }
}
console.log('PASS: installer product names, app identifiers and platform icons follow the release channel.');
