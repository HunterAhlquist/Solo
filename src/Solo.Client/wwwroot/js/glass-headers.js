// In the macOS glass shell the board's column strip carries the one frosted
// layer behind the board's sticky headers, and a group header pinned under the
// strip has to share it — two blurs meet in a seam (see app.css, "The board's
// sticky headers"). CSS can't tell when a sticky box is stuck, so this measures
// which group header sits at the strip's bottom edge and stretches the strip's
// layer down to cover it. A header on its way out, pushed up by the end of its
// group, is clipped at that edge so it slides under the column labels rather
// than over them. Outside the glass shell, and in the phone layout where the
// strip is hidden, it leaves everything as it found it.
(() => {
    const EXTENT = '--solo-glass-header-extent';
    let frame = 0;
    let clipped = null;

    const clip = (header, inset) => {
        if (clipped && clipped !== header) clipped.style.clipPath = '';
        clipped = header;
        if (header) header.style.clipPath = inset > 0 ? `inset(${inset}px 0 0 0)` : '';
    };

    const update = () => {
        frame = 0;
        const strip = document.querySelector('.kanban-header');
        if (!strip) {
            clip(null);
            return;
        }

        const edge = strip.getBoundingClientRect().bottom;
        let extent = 0;
        let leaving = null;
        let inset = 0;
        if (document.documentElement.classList.contains('solo-desktop--glass') && strip.offsetHeight > 0) {
            // A pinned header's top is the strip's bottom; the tolerance absorbs
            // sub-pixel rounding between the two sticky offsets.
            for (const header of document.querySelectorAll('.epic-group__header')) {
                const box = header.getBoundingClientRect();
                if (box.top <= edge + 1 && box.bottom > edge + 1) {
                    extent = box.bottom - edge;
                    leaving = header;
                    inset = edge - box.top;
                    break;
                }
            }
        }

        const value = `${Math.max(0, Math.round(extent))}px`;
        if (strip.style.getPropertyValue(EXTENT) !== value) strip.style.setProperty(EXTENT, value);
        clip(leaving, inset > 0.5 ? inset : 0);
    };

    const schedule = () => {
        if (!frame) frame = requestAnimationFrame(update);
    };

    // Scroll moves the headers; a resize of the page (a group collapsing, the
    // board rendering, the window changing) can move them without one.
    addEventListener('scroll', schedule, { passive: true, capture: true });
    addEventListener('resize', schedule, { passive: true });
    const start = () => new ResizeObserver(schedule).observe(document.body);
    if (document.body) start();
    else addEventListener('DOMContentLoaded', start, { once: true });
})();
