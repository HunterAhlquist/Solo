// The Roadmaps timeline's browser half: scroll reporting for virtualization,
// and the pointer gestures a Blazor event can't express — dragging a bar up or
// down to reorder it or sideways to move its dates, dragging its edges to
// resize it, the hover marker over the empty rows, and the ticker that scrolls
// a label too long for its bar. Self-contained on purpose: core's js/drag.js and DragBridge
// belong to Solo.Client, and an extension can't count on either. Loaded as a
// module from the package's content/ folder (same-origin, so script-src 'self'
// covers it on the web and through the desktop shell's proxy alike).
//
// Every attribute this module reads is data-roadmap-*, so core's drag engine
// (which looks for data-drag-item) never mistakes a row for one of its cards.

const MOVE_SLOP_PX = 5;      // a mouse or pen press becomes a drag past this
const HOLD_MS = 320;         // a finger has to hold still this long first...
const HOLD_SLOP_PX = 10;     // ...moving less than this, or it was a scroll
const EDGE_PX = 40;          // auto-scroll zone at each edge of the timeline
const EDGE_SPEED_PX = 12;
const TICKER_GAP_PX = 32;    // between the end of a name and its copy
const TICKER_SPEED_PX = 45;  // per second

/** The live wiring per grid, so attaching one again replaces it rather than doubling it. */
const attached = new WeakMap();

/** Adds the package's stylesheet once per page load. */
export function ensureStyles(href) {
    if (document.querySelector('link[data-roadmap-styles]')) {
        return;
    }

    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = href;
    link.dataset.roadmapStyles = '';
    document.head.appendChild(link);
}

/** Keeps a keyboard-highlighted row in view; takes an element or a selector. */
export function scrollIntoView(target) {
    const element = typeof target === 'string' ? document.querySelector(target) : target;
    element?.scrollIntoView({ block: 'nearest' });
}

/**
 * Wires a timeline. `options`: dayWidth, rowHeight, days, entries (how many
 * rows hold a task), canWrite, firstDay (ISO date of column 0). Returns a
 * handle .NET keeps: scrollToDay, refresh, configure, dispose.
 */
export function attach(scroller, dotnet, options) {
    attached.get(scroller)?.dispose();
    const state = { ...options };
    let frame = 0;
    let press = null;
    let autoScroll = 0;
    let label = null;   // the floating date beside a bar being moved or resized
    let disposed = false;

    // The page can go away between a gesture and its callback — navigating off
    // mid-scroll, say, or a ResizeObserver firing as the grid is torn down. A
    // call into a disposed component has nowhere to land, and isn't an error;
    // anything else still reaches the console.
    const call = (method, ...args) => disposed
        ? Promise.resolve()
        : dotnet.invokeMethodAsync(method, ...args).catch(error => {
            if (!disposed) {
                console.error(`roadmap: ${method} failed`, error);
            }
        });

    // Tells .NET while a bar is being dragged or resized: others see this person
    // editing, and a live change waits until the bar lands rather than
    // redrawing the rows out from under the pointer.
    let gesturing = false;
    const setGesturing = active => {
        if (gesturing !== active) {
            gesturing = active;
            call('OnGesture', active);
        }
    };

    const body = () => scroller.querySelector('[data-roadmap-body]');
    const rows = () => Array.from(scroller.querySelectorAll('[data-roadmap-entry]'));

    // -- Viewport ------------------------------------------------------------

    const visibleDays = () => [
        Math.floor(scroller.scrollLeft / state.dayWidth),
        Math.ceil((scroller.scrollLeft + scroller.clientWidth) / state.dayWidth) - 1,
    ];

    /** Which rows' bars are wholly off to one side, for the edge hints. */
    const updateOffscreen = () => {
        const [first, last] = visibleDays();
        for (const row of rows()) {
            const start = Number(row.dataset.start);
            const end = Number(row.dataset.end);
            const side = end < first ? 'before' : start > last ? 'after' : '';
            if (row.dataset.offscreen !== side) {
                row.dataset.offscreen = side;
            }
        }
    };

    const report = () => {
        frame = 0;
        updateOffscreen();
        call('OnViewport', scroller.scrollLeft, scroller.clientWidth);
    };

    const onScroll = () => {
        if (!frame) {
            frame = requestAnimationFrame(report);
        }
    };

    scroller.addEventListener('scroll', onScroll, { passive: true });
    const resizeObserver = new ResizeObserver(onScroll);
    resizeObserver.observe(scroller);

    // -- Geometry --------------------------------------------------------------

    const dayAtClientX = clientX => {
        const rect = body().getBoundingClientRect();
        return Math.min(Math.max(Math.floor((clientX - rect.left) / state.dayWidth), 0), state.days - 1);
    };

    const rowAtClientY = clientY => {
        const rect = body().getBoundingClientRect();
        return Math.floor((clientY - rect.top) / state.rowHeight);
    };

    const formatDay = index => {
        const [y, m, d] = state.firstDay.split('-').map(Number);
        return new Date(y, m - 1, d + index)
            .toLocaleDateString(undefined, { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric' });
    };

    const formatShort = index => {
        const [y, m, d] = state.firstDay.split('-').map(Number);
        return new Date(y, m - 1, d + index).toLocaleDateString(undefined, { day: 'numeric', month: 'short' });
    };

    // -- Hover marker: where a click adds a task ---------------------------------

    // Only the empty rows below the last task: a new task takes a row of its own
    // at the end, so a plus beside an existing bar would promise a place it
    // can't have.
    const isSpareRow = clientY => rowAtClientY(clientY) >= state.entries;

    const marker = () => scroller.querySelector('[data-roadmap-marker]');

    const hideMarker = () => marker()?.classList.remove('is-visible');

    const onHover = e => {
        const target = marker();
        if (!target || !state.canWrite || press || e.pointerType !== 'mouse' ||
            !e.target.closest('[data-roadmap-body]') || !isSpareRow(e.clientY)) {
            hideMarker();
            return;
        }

        target.style.left = `${dayAtClientX(e.clientX) * state.dayWidth}px`;
        target.style.top = `${rowAtClientY(e.clientY) * state.rowHeight}px`;
        target.classList.add('is-visible');
    };

    // -- Ticker: a hovered label too long for its bar scrolls past -----------------

    const reducedMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)');
    let ticking = null;

    const stopTicker = () => {
        if (ticking) {
            delete ticking.dataset.ticker;
            ticking = null;
        }
    };

    const onOver = e => {
        const label = press || e.pointerType === 'touch'
            ? null
            : e.target.closest('[data-roadmap-bar]')?.querySelector('.roadmap-bar__label');
        if (label === ticking) {
            return;
        }

        stopTicker();
        if (!label || reducedMotion?.matches) {
            return;
        }

        // Ticking lays the text out at its full width, which is the measure
        // wanted: if it fits the room the bar gives the label, nothing was cut.
        label.style.setProperty('--roadmap-ticker-gap', `${TICKER_GAP_PX}px`);
        label.dataset.ticker = '';
        const distance = label.querySelector('.roadmap-bar__text').getBoundingClientRect().width;
        if (distance - TICKER_GAP_PX <= label.clientWidth + 0.5) {
            delete label.dataset.ticker;
            return;
        }

        label.style.setProperty('--roadmap-ticker-distance', `${distance}px`);
        label.style.setProperty('--roadmap-ticker-duration', `${distance / TICKER_SPEED_PX}s`);
        ticking = label;
    };

    const onLeave = () => {
        hideMarker();
        stopTicker();
    };

    // -- Presses -----------------------------------------------------------------

    const onDown = e => {
        if (e.pointerType === 'mouse' && e.button !== 0) {
            return;
        }

        const target = e.target;
        if (target.closest('[data-roadmap-menu], [data-roadmap-hint]')) {
            return;
        }

        const bar = target.closest('[data-roadmap-bar]');
        const row = target.closest('[data-roadmap-entry]');
        const handle = target.closest('[data-roadmap-handle]');

        if (handle && bar && row && state.canWrite) {
            startResize(e, handle, bar, row);
            return;
        }

        if (bar && row) {
            press = {
                mode: 'pending',
                pointerId: e.pointerId,
                type: e.pointerType,
                x0: e.clientX, y0: e.clientY, x: e.clientX, y: e.clientY,
                bar, row,
                entryId: row.dataset.roadmapEntry,
                hold: null,
                lifted: false,
            };
            if (e.pointerType === 'touch' && state.canWrite) {
                press.hold = setTimeout(() => press && lift(), HOLD_MS);
            }
            listen();
            return;
        }

        if (target.closest('[data-roadmap-body]') && state.canWrite && isSpareRow(e.clientY)) {
            press = {
                mode: 'empty',
                pointerId: e.pointerId,
                type: e.pointerType,
                x0: e.clientX, y0: e.clientY, x: e.clientX, y: e.clientY,
            };
            listen();
        }
    };

    const onMove = e => {
        if (!press || e.pointerId !== press.pointerId) {
            return;
        }

        press.x = e.clientX;
        press.y = e.clientY;
        const travelled = Math.hypot(e.clientX - press.x0, e.clientY - press.y0);

        switch (press.mode) {
            case 'pending':
                if (press.type === 'touch') {
                    if (!press.lifted && travelled > HOLD_SLOP_PX) {
                        abandon(); // a scroll all along
                    } else if (press.lifted && travelled > HOLD_SLOP_PX) {
                        startGesture();
                    }
                } else if (travelled > MOVE_SLOP_PX && state.canWrite) {
                    startGesture();
                }
                break;
            case 'drag':
                moveDrag();
                break;
            case 'move':
                moveShift();
                break;
            case 'resize':
                moveResize();
                break;
            case 'empty':
                if (travelled > HOLD_SLOP_PX) {
                    abandon();
                }
                break;
        }
    };

    const onUp = async e => {
        if (!press || e.pointerId !== press.pointerId) {
            return;
        }

        const ended = press;
        unlisten();
        clearTimeout(ended.hold);
        press = null;

        switch (ended.mode) {
            case 'pending':
                if (ended.lifted) {
                    // Held without moving: a long press, which on a touchscreen is
                    // the context menu.
                    ended.row.classList.remove('is-dragging');
                    await call('OnBarMenu', ended.entryId, ended.x, ended.y);
                } else {
                    await call('OnBarClick', ended.entryId);
                }
                break;
            case 'drag':
                await endDrag(ended);
                setGesturing(false);
                break;
            case 'move':
                await endMove(ended);
                setGesturing(false);
                break;
            case 'resize':
                await endResize(ended);
                setGesturing(false);
                break;
            case 'empty':
                await call('OnDayClick', dayAtClientX(ended.x));
                break;
        }
    };

    const onCancel = e => {
        if (press && e.pointerId === press.pointerId) {
            abandon();
        }
    };

    const abandon = () => {
        if (!press) {
            return;
        }

        clearTimeout(press.hold);
        if (press.mode === 'drag') {
            clearDrag(press);
        } else if (press.mode === 'move') {
            clearMove(press);
        } else if (press.mode === 'resize') {
            clearResize(press);
        } else if (press.lifted) {
            press.row.classList.remove('is-dragging');
        }

        unlisten();
        press = null;
        setGesturing(false);
    };

    /** Touch only: the hold is up, so this press is the timeline's, not the page's. */
    const lift = () => {
        press.lifted = true;
        press.row.classList.add('is-dragging');
        navigator.vibrate?.(10);
    };

    /**
     * Refuses the browser's scroll once a finger has taken a row. Non-passive,
     * and attached only for the length of a press — a permanent one takes every
     * scroll on the page off the fast path.
     */
    const refuseScroll = e => {
        if (press && (press.mode === 'drag' || press.mode === 'move' || press.mode === 'resize' || press.lifted)) {
            e.preventDefault();
        }
    };

    const listen = () => {
        document.addEventListener('touchmove', refuseScroll, { passive: false });
        window.addEventListener('pointermove', onMove);
        window.addEventListener('pointerup', onUp);
        window.addEventListener('pointercancel', onCancel);
    };

    const unlisten = () => {
        document.removeEventListener('touchmove', refuseScroll, { passive: false });
        window.removeEventListener('pointermove', onMove);
        window.removeEventListener('pointerup', onUp);
        window.removeEventListener('pointercancel', onCancel);
        cancelAnimationFrame(autoScroll);
        autoScroll = 0;
    };

    // -- Dragging a bar: up and down reorders, sideways moves its dates -------------

    // The way the pointer first goes decides which, for the whole gesture: a
    // reorder that drifted half a column sideways would otherwise move someone's
    // deadline as a side effect.
    const startGesture = () => {
        stopTicker();
        setGesturing(true);
        if (Math.abs(press.x - press.x0) > Math.abs(press.y - press.y0)) {
            startMove();
        } else {
            startDrag();
        }
    };

    const capture = () => {
        try {
            press.bar.setPointerCapture(press.pointerId);
        } catch {
            // Released already, or the element was replaced; window listeners cover it.
        }
    };

    const indicator = () => scroller.querySelector('[data-roadmap-drop]');

    const startDrag = () => {
        press.mode = 'drag';
        press.list = rows();
        press.from = press.list.indexOf(press.row);
        press.gap = press.from;
        press.scrollTop0 = scroller.scrollTop;
        capture();

        press.row.classList.add('is-dragging');
        hideMarker();
        moveDrag();
        autoScroll = requestAnimationFrame(scrollNearEdges);
    };

    const moveDrag = () => {
        const dy = press.y - press.y0 + (scroller.scrollTop - press.scrollTop0);
        press.row.style.setProperty('transform', `translateY(${dy}px)`);

        const rect = body().getBoundingClientRect();
        const gap = Math.min(Math.max(Math.round((press.y - rect.top) / state.rowHeight), 0), press.list.length);
        press.gap = gap;
        const line = indicator();
        if (line) {
            line.style.top = `${gap * state.rowHeight}px`;
            line.classList.toggle('is-visible', gap !== press.from && gap !== press.from + 1);
        }
    };

    const scrollNearEdges = () => {
        if (!press || press.mode !== 'drag') {
            return;
        }

        const rect = scroller.getBoundingClientRect();
        const headHeight = scroller.querySelector('[data-roadmap-head]')?.offsetHeight ?? 0;
        if (press.y < rect.top + headHeight + EDGE_PX) {
            scroller.scrollTop -= EDGE_SPEED_PX;
            moveDrag();
        } else if (press.y > rect.bottom - EDGE_PX) {
            scroller.scrollTop += EDGE_SPEED_PX;
            moveDrag();
        }

        autoScroll = requestAnimationFrame(scrollNearEdges);
    };

    const startMove = () => {
        press.mode = 'move';
        press.start = Number(press.row.dataset.start);
        press.end = Number(press.row.dataset.end);
        press.scrollLeft0 = scroller.scrollLeft;
        press.shift = 0;
        capture();

        press.row.classList.add('is-dragging');
        label = document.createElement('div');
        label.className = 'roadmap-resize-label';
        document.body.appendChild(label);
        hideMarker();
        moveShift();
        autoScroll = requestAnimationFrame(scrollNearSides);
    };

    /**
     * Snaps to whole days, and stops at the window's edges. The preview offset
     * goes on the bar, not the row: when .NET redraws the bar in its new place
     * it rewrites the bar's style, which drops the offset in the same update — on
     * the row it would outlive the redraw for a frame and add to the new place.
     */
    const moveShift = () => {
        const dx = press.x - press.x0 + (scroller.scrollLeft - press.scrollLeft0);
        press.shift = Math.min(Math.max(Math.round(dx / state.dayWidth), -press.start), state.days - 1 - press.end);
        press.bar.style.setProperty('transform', `translateX(${press.shift * state.dayWidth}px)`);

        const from = press.start + press.shift;
        const to = press.end + press.shift;
        const barRect = press.bar.getBoundingClientRect();
        label.textContent = from === to ? formatShort(from) : `${formatShort(from)} – ${formatShort(to)}`;
        label.style.left = `${barRect.left}px`;
        label.style.top = `${barRect.top}px`;
    };

    const scrollNearSides = () => {
        if (!press || press.mode !== 'move') {
            return;
        }

        const rect = scroller.getBoundingClientRect();
        if (press.x < rect.left + EDGE_PX) {
            scroller.scrollLeft -= EDGE_SPEED_PX;
            moveShift();
        } else if (press.x > rect.right - EDGE_PX) {
            scroller.scrollLeft += EDGE_SPEED_PX;
            moveShift();
        }

        autoScroll = requestAnimationFrame(scrollNearSides);
    };

    const clearMove = ended => {
        ended.bar.style.removeProperty('transform');
        ended.row.classList.remove('is-dragging');
        label?.remove();
        label = null;
    };

    const endMove = async ended => {
        try {
            if (ended.shift !== 0) {
                // As with a resize, .NET redraws the bar in its new place before
                // this returns, so dropping the preview doesn't flash the old one.
                await call('OnMove', ended.entryId, ended.shift);
            }
        } finally {
            clearMove(ended);
        }
    };

    const clearDrag = ended => {
        ended.row.style.removeProperty('transform');
        ended.row.classList.remove('is-dragging');
        indicator()?.classList.remove('is-visible');
    };

    const endDrag = async ended => {
        // The gap counts slots in the whole list, the moved row included; the
        // index .NET wants is among the others.
        const to = ended.gap > ended.from ? ended.gap - 1 : ended.gap;
        try {
            if (to !== ended.from) {
                await call('OnRowDrop', ended.entryId, to);
            }
        } finally {
            clearDrag(ended);
        }
    };

    // -- Resizing bars -------------------------------------------------------------

    const startResize = (e, handle, bar, row) => {
        e.preventDefault();
        press = {
            mode: 'resize',
            pointerId: e.pointerId,
            type: e.pointerType,
            x0: e.clientX, y0: e.clientY, x: e.clientX, y: e.clientY,
            edge: handle.dataset.roadmapHandle,
            bar, row,
            entryId: row.dataset.roadmapEntry,
            start: Number(row.dataset.start),
            end: Number(row.dataset.end),
        };
        press.newStart = press.start;
        press.newEnd = press.end;
        try {
            handle.setPointerCapture(e.pointerId);
        } catch {
            // Window listeners cover it.
        }

        bar.classList.add('is-resizing');
        stopTicker();
        setGesturing(true);
        label = document.createElement('div');
        label.className = 'roadmap-resize-label';
        document.body.appendChild(label);
        hideMarker();
        listen();
        moveResize();
    };

    const moveResize = () => {
        const delta = Math.round((press.x - press.x0) / state.dayWidth);
        if (press.edge === 'start') {
            press.newStart = Math.min(Math.max(press.start + delta, 0), press.end);
        } else {
            press.newEnd = Math.min(Math.max(press.end + delta, press.start), state.days - 1);
        }

        const style = press.bar.style;
        style.setProperty('--roadmap-preview-left', `${press.newStart * state.dayWidth}px`);
        style.setProperty('--roadmap-preview-width', `${(press.newEnd - press.newStart + 1) * state.dayWidth}px`);

        const barRect = press.bar.getBoundingClientRect();
        label.textContent = formatDay(press.edge === 'start' ? press.newStart : press.newEnd);
        label.style.left = `${press.edge === 'start' ? barRect.left : barRect.right}px`;
        label.style.top = `${barRect.top}px`;
    };

    const clearResize = ended => {
        ended.bar.style.removeProperty('--roadmap-preview-left');
        ended.bar.style.removeProperty('--roadmap-preview-width');
        ended.bar.classList.remove('is-resizing');
        label?.remove();
        label = null;
    };

    const endResize = async ended => {
        const index = ended.edge === 'start' ? ended.newStart : ended.newEnd;
        const moved = ended.edge === 'start' ? index !== ended.start : index !== ended.end;
        try {
            if (moved) {
                // .NET redraws the bar at its new place before this returns, so
                // dropping the preview afterwards doesn't flash the old one.
                await call('OnResize', ended.entryId, ended.edge, index);
            }
        } finally {
            clearResize(ended);
        }
    };

    // -- Menus and hints ---------------------------------------------------------

    const onContextMenu = e => {
        const row = e.target.closest('[data-roadmap-entry]');
        if (!row || !e.target.closest('[data-roadmap-bar]')) {
            return;
        }

        e.preventDefault();
        // On a touchscreen the long press already did this.
        if (e.pointerType !== 'touch' && !(press && press.type === 'touch')) {
            call('OnBarMenu', row.dataset.roadmapEntry, e.clientX, e.clientY);
        }
    };

    const onClick = e => {
        const hint = e.target.closest('[data-roadmap-hint]');
        if (!hint) {
            return;
        }

        const row = hint.closest('[data-roadmap-entry]');
        const start = Number(row.dataset.start);
        scroller.scrollTo({ left: Math.max(start - 2, 0) * state.dayWidth, behavior: 'smooth' });
    };

    // WebKit runs a long-press drag of its own on anything it thinks is draggable.
    const onDragStart = e => {
        if (e.target instanceof Element && e.target.closest('[data-roadmap-bar]')) {
            e.preventDefault();
        }
    };

    scroller.addEventListener('pointerdown', onDown);
    scroller.addEventListener('pointermove', onHover);
    scroller.addEventListener('pointerover', onOver);
    scroller.addEventListener('pointerleave', onLeave);
    scroller.addEventListener('contextmenu', onContextMenu);
    scroller.addEventListener('click', onClick);
    scroller.addEventListener('dragstart', onDragStart);

    report();

    const handle = {
        scrollToDay: index => {
            scroller.scrollLeft = Math.max(index, 0) * state.dayWidth;
            report();
        },
        refresh: () => updateOffscreen(),
        configure: next => Object.assign(state, next),
        dispose: () => {
            disposed = true;
            abandon();
            cancelAnimationFrame(frame);
            resizeObserver.disconnect();
            scroller.removeEventListener('scroll', onScroll);
            stopTicker();
            scroller.removeEventListener('pointerdown', onDown);
            scroller.removeEventListener('pointermove', onHover);
            scroller.removeEventListener('pointerover', onOver);
            scroller.removeEventListener('pointerleave', onLeave);
            scroller.removeEventListener('contextmenu', onContextMenu);
            scroller.removeEventListener('click', onClick);
            scroller.removeEventListener('dragstart', onDragStart);
            if (attached.get(scroller) === handle) {
                attached.delete(scroller);
            }
        },
    };
    attached.set(scroller, handle);
    return handle;
}
