export function ensureStyles(url) {
    if (document.querySelector('link[data-design-docs]')) return;
    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = url;
    link.dataset.designDocs = '';
    document.head.append(link);
}
export { mountEditor } from './editor/index.js';
