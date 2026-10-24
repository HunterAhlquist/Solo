import { build } from 'esbuild';
import { readFile, writeFile, copyFile, mkdir, readdir, rm } from 'node:fs/promises';
import { join } from 'node:path';

const output = '../../Solo.DesignDocs/content';
await rm(output, { recursive: true, force: true });
await mkdir(output, { recursive: true });
await build({ entryPoints: ['index.js'], bundle: true, format: 'esm', target: ['safari15.4', 'chrome111'],
    outfile: `${output}/design-docs.js`, minify: true, legalComments: 'eof' });
await copyFile('design-docs.css', `${output}/design-docs.css`);
for(const file of ['explosion.gif','explosion.wav','NOTICE.txt','MATERIAL-SYMBOLS-LICENSE.txt'])await copyFile(`assets/${file}`, `${output}/${file}`);

const lock = JSON.parse(await readFile('package-lock.json', 'utf8'));
const notices = [];
for (const path of Object.keys(lock.packages).filter(p => p.startsWith('node_modules/'))) {
    if (path.includes('@esbuild/')) continue;
    const pkg = JSON.parse(await readFile(join(path, 'package.json'), 'utf8'));
    const files = await readdir(path);
    const license = files.find(f => /^licen[sc]e(\.|$)/i.test(f));
    if (!license) throw new Error(`No license file for ${pkg.name}`);
    notices.push(`${pkg.name} ${pkg.version} (${pkg.license})\n${await readFile(join(path, license), 'utf8')}`);
}
await writeFile(`${output}/THIRD-PARTY-LICENSES.txt`, notices.join('\n\n--------------------\n\n'));
