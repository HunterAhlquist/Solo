// Full-screen view of one image, or a carousel through a stack, over a
// darkened page. Built from DOM calls only: nothing from a note ever goes
// through innerHTML.

export function openLightbox(urls, start = 0, captions = []) {
    let index = Math.min(Math.max(start, 0), urls.length - 1);
    const overlay = el('div', 'dd-lightbox');
    overlay.setAttribute('role', 'dialog');
    overlay.setAttribute('aria-modal', 'true');
    const image = el('img', 'dd-lightbox__image');
    const caption = el('div', 'dd-lightbox__caption');
    const close = button('dd-lightbox__close', '✕', 'Close');
    overlay.append(image, caption, close);

    let previous = null;
    let next = null;
    if (urls.length > 1) {
        previous = button('dd-lightbox__nav dd-lightbox__nav--previous', '‹', 'Previous image');
        next = button('dd-lightbox__nav dd-lightbox__nav--next', '›', 'Next image');
        overlay.append(previous, next);
    }

    const show = async () => {
        image.src = await urls[index];
        caption.textContent = urls.length > 1
            ? `${captions[index] ?? ''} ${index + 1} / ${urls.length}`.trim()
            : captions[index] ?? '';
    };
    const go = step => {
        index = (index + step + urls.length) % urls.length;
        show();
    };
    const dismiss = () => {
        overlay.remove();
        document.removeEventListener('keydown', onKey, true);
    };
    const onKey = e => {
        if (e.key === 'Escape') {
            e.preventDefault();
            dismiss();
        } else if (e.key === 'ArrowRight' && urls.length > 1) {
            go(1);
        } else if (e.key === 'ArrowLeft' && urls.length > 1) {
            go(-1);
        }
    };

    close.addEventListener('click', dismiss);
    previous?.addEventListener('click', e => { e.stopPropagation(); go(-1); });
    next?.addEventListener('click', e => { e.stopPropagation(); go(1); });
    overlay.addEventListener('click', e => { if (e.target === overlay) dismiss(); });
    document.addEventListener('keydown', onKey, true);

    // A swipe pages through a stack on touch.
    let startX = null;
    overlay.addEventListener('pointerdown', e => { startX = e.clientX; });
    overlay.addEventListener('pointerup', e => {
        if (startX !== null && urls.length > 1 && Math.abs(e.clientX - startX) > 50) {
            go(e.clientX < startX ? 1 : -1);
        }

        startX = null;
    });

    document.body.append(overlay);
    close.focus();
    show();
    return dismiss;
}

function el(tag, className) {
    const node = document.createElement(tag);
    node.className = className;
    return node;
}

function button(className, text, label) {
    const node = el('button', className);
    node.type = 'button';
    node.textContent = text;
    node.setAttribute('aria-label', label);
    return node;
}
