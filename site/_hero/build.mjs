// Bundles the front page's 3D mark with the parts of three.js it uses into one file the
// page loads as a module: ../assets/js/hero-logo.js. three.js ships no minified build, and
// the page has no import map to find it by name with, so the two are put together here
// and what the scene doesn't use is left out.
//
// The licences of what is bundled are kept at the end of the file.
import { build } from 'esbuild';

await build({
    entryPoints: ['hero-logo.js'],
    bundle: true,
    format: 'esm',
    target: ['safari15.4', 'chrome111', 'firefox115'],
    outfile: '../assets/js/hero-logo.js',
    minify: true,
    legalComments: 'eof',
});
