export function ensureStyles(url) {
    if (document.querySelector('link[data-design-docs]')) return;
    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = url;
    link.dataset.designDocs = '';
    document.head.append(link);
}
export { mountEditor } from './editor/index.js';
export { mountSketch } from './sketch/index.js';
export { createTransfer } from './io/vault.js';

export { mountTree } from './tree.js';
export const isNarrowWorkspace=()=>matchMedia('(max-width:600px)').matches;
