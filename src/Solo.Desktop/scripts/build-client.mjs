// Publishes Solo.Client and copies its wwwroot into the shell, which serves
// it verbatim — the desktop app ships the exact same bytes as the hosted web
// build.
import { execSync } from 'node:child_process';
import { cpSync, rmSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const desktopDir = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const clientProject = path.join(desktopDir, '..', 'Solo.Client', 'Solo.Client.csproj');
const publishDir = path.join(desktopDir, 'publish');
const targetDir = path.join(desktopDir, 'wwwroot');

console.log('Publishing Solo.Client…');
execSync(`dotnet publish "${clientProject}" -c Release -o "${publishDir}"`, {
    stdio: 'inherit',
});

const sourceWwwroot = path.join(publishDir, 'wwwroot');
if (!existsSync(sourceWwwroot)) {
    console.error(`Publish produced no wwwroot at ${sourceWwwroot}`);
    process.exit(1);
}

console.log(`Copying client assets to ${targetDir}…`);
rmSync(targetDir, { recursive: true, force: true });
cpSync(sourceWwwroot, targetDir, { recursive: true });
rmSync(publishDir, { recursive: true, force: true });

console.log('Done. Run `npm start` to launch the desktop app.');
