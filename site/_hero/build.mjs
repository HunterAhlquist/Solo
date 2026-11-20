// Bundles the front page's 3D mark with the parts of its libraries it uses into one file
// the page loads as a module: ../assets/js/hero-logo.js. three.js ships no minified build,
// and the page has no import map to find a library by name with, so they are put together
// here and what the scene doesn't use is left out.
//
// The licences of what is bundled are kept at the end of the file: the notices the
// libraries carry in their own source, which esbuild keeps, and then each one's licence
// in full, since not every library carries a notice.
import { build } from 'esbuild';
import { appendFile, readFile } from 'node:fs/promises';

const bundled = ['three', 'cannon-es'];

await build({
    entryPoints: ['hero-logo.js'],
    bundle: true,
    format: 'esm',
    target: ['safari15.4', 'chrome111', 'firefox115'],
    outfile: '../assets/js/hero-logo.js',
    minify: true,
    legalComments: 'eof',
});

const licences = [];
for (const name of bundled) {
    const version = JSON.parse(await readFile(`node_modules/${name}/package.json`, 'utf8')).version;
    licences.push(`${name} ${version}\n\n${(await readFile(`node_modules/${name}/LICENSE`, 'utf8')).trim()}`);
}
await appendFile('../assets/js/hero-logo.js', `/*\n${licences.join('\n\n--------------------\n\n').replaceAll('*/', '* /')}\n*/\n`);
