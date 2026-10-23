// Bundles the browser module into the server package's content folder, which
// the host serves at /api/extensions/design-docs/content/. One ES module with
// no chunks: the CSP admits same-origin script only, and a single file keeps
// the package's content list fixed.

import { build } from 'esbuild';
import { copyFile, mkdir, readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const here = path => fileURLToPath(new URL(path, import.meta.url));
const content = here('../../Solo.DesignDocs/content/');
const font = here('node_modules/@fontsource/silkscreen/');

await mkdir(content, { recursive: true });
await build({
    entryPoints: [here('src/index.js')],
    outfile: `${content}design-docs.js`,
    bundle: true,
    format: 'esm',
    target: 'es2022',
    minify: true,
    legalComments: 'eof',
    logLevel: 'warning',
});
await copyFile(here('src/styles.css'), `${content}design-docs.css`);
await copyFile(`${font}files/silkscreen-latin-400-normal.woff2`, `${content}silkscreen.woff2`);
await copyFile(`${font}LICENSE`, `${content}silkscreen-LICENSE.txt`);

// The page's CSP has no 'unsafe-eval'; a dependency that reached for it would
// fail only at runtime, in the one code path that used it.
const bundle = await readFile(`${content}design-docs.js`, 'utf8');
if (/\beval\(|new Function\(/.test(bundle)) {
    throw new Error('The bundle calls eval or new Function, which the CSP refuses.');
}
