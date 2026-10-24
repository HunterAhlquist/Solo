import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';

// macOS associates Local Network permission with the launcher UUID (Apple TN3178/TN3179).
// Assign unique UUIDs before signing. Preserve framework UUIDs and their debug symbols.
export default async function afterPack(context) {
    if (context.electronPlatformName !== 'darwin') return;
    const app = path.join(context.appOutDir, context.packager.appInfo.productFilename + '.app');
    const frameworks = path.join(app, 'Contents', 'Frameworks');
    const helpers = fs.existsSync(frameworks) ? fs.readdirSync(frameworks)
        .filter(name => name.endsWith('.app')).map(name => path.join(frameworks, name)) : [];
    for (const bundle of [app, ...helpers]) {
        const executableName = execFileSync('/usr/libexec/PlistBuddy', [
            '-c', 'Print :CFBundleExecutable', path.join(bundle, 'Contents', 'Info.plist'),
        ], { encoding: 'utf8' }).trim();
        if (path.basename(executableName) !== executableName) throw new Error('Invalid bundle executable name.');
        const executable = path.join(bundle, 'Contents', 'MacOS', executableName);
        const data = fs.readFileSync(executable);
        if (data.length < 32 || data.readUInt32LE(0) !== 0xfeedfacf) {
            throw new Error(`Expected a thin 64-bit Mach-O launcher: ${executableName}`);
        }
        const count = data.readUInt32LE(16);
        const end = 32 + data.readUInt32LE(20);
        if (end > data.length) throw new Error('Invalid Mach-O load-command bounds.');
        let offset = 32;
        let uuidOffset;
        for (let index = 0; index < count; index++) {
            if (offset + 8 > end) throw new Error('Truncated Mach-O load command.');
            const command = data.readUInt32LE(offset);
            const size = data.readUInt32LE(offset + 4);
            if (size < 8 || offset + size > end) throw new Error('Invalid Mach-O load command.');
            if (command === 0x1b) {
                if (size !== 24 || uuidOffset !== undefined) throw new Error('Invalid Mach-O LC_UUID.');
                uuidOffset = offset + 8;
            }
            offset += size;
        }
        if (uuidOffset === undefined || offset !== end) throw new Error('Launcher has no valid LC_UUID.');
        const uuid = crypto.randomUUID();
        Buffer.from(uuid.replaceAll('-', ''), 'hex').copy(data, uuidOffset);
        fs.writeFileSync(executable, data);
        console.log(`Assigned build UUID ${uuid} to ${executableName} before signing.`);
    }
}
