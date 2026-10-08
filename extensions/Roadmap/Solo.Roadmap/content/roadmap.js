// The Roadmaps timeline's browser half: scroll reporting for virtualization,
// and the pointer gestures a Blazor event can't express — dragging a bar up or
// down onto another row or between two, or sideways to move its dates, dragging
// its edges to resize it, dragging a milestone's flag or line to another day, selecting
// bars with a box or a modifier-click, the right-clicks that open the bar,
// milestone and day menus, and the ticker that scrolls a label too long for
// its bar. Self-contained on purpose: core's
// js/drag.js and DragBridge belong to Solo.Client, and an extension can't count
// on either. Loaded as a
// module from the package's content/ folder (same-origin, so script-src 'self'
// covers it on the web and through the desktop shell's proxy alike).
//
// Every attribute this module reads is data-roadmap-*, so core's drag engine
// (which looks for data-drag-item) never mistakes a bar for one of its cards.

const MOVE_SLOP_PX = 5;      // a mouse or pen press becomes a drag past this
const HOLD_MS = 320;         // a finger has to hold still this long first...
const HOLD_SLOP_PX = 10;     // ...moving less than this, or it was a scroll
const EDGE_PX = 40;          // auto-scroll zone at each edge of the timeline
const EDGE_SPEED_PX = 12;
const TICKER_GAP_PX = 32;    // between the end of a name and its copy
const TICKER_SPEED_PX = 45;  // per second
const JOIN_FROM = 0.25;      // a drag joins the row under it from this far into a line to as far from its end
const HINT_INSET_PX = 8;     // a hint's inset from the edge, plus a gap before the first bar in view
const HINT_MIN_PX = 48;      // narrower than this, a hint shows nothing worth reading

// A Mac's Control-click is its right-click, so selection there is Command's;
// everywhere else it's Control's, as in a file manager.
const IS_MAC = /Mac|iPhone|iPad|iPod/.test(navigator.platform);
const toggles = e => IS_MAC ? e.metaKey : e.ctrlKey;

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

/**
 * Wires a timeline. `options`: dayWidth, rowHeight (a line's), days, canWrite,
 * firstDay (ISO date of column 0). Returns a handle .NET keeps: scrollToDay,
 * revealLine, refresh, configure, dispose.
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

    // A bar's element is its entry's, and is always there, drawn or not; its
    // line is where it's drawn and its row is which tasks it shares one with.
    const body = () => scroller.querySelector('[data-roadmap-body]');
    const entries = () => Array.from(scroller.querySelectorAll('[data-roadmap-entry]'));
    const lines = () => Array.from(scroller.querySelectorAll('[data-roadmap-line]'));
    const startOf = element => Number(element.dataset.start);
    const endOf = element => Number(element.dataset.end);
    const overlap = (a, b) => startOf(a) <= endOf(b) && startOf(b) <= endOf(a);

    // -- Viewport ------------------------------------------------------------

    const visibleDays = () => [
        Math.floor(scroller.scrollLeft / state.dayWidth),
        Math.ceil((scroller.scrollLeft + scroller.clientWidth) / state.dayWidth) - 1,
    ];

    /**
     * The edge hints, and which milestones are wholly off to one side, for the
     * chips in the month band. A milestone's line is at the end of its day, so
     * it's in view while that day is.
     */
    const updateOffscreen = () => {
        const [first, last] = visibleDays();
        for (const hint of scroller.querySelectorAll('[data-roadmap-milestone-hint]')) {
            const day = Number(hint.dataset.day);
            const side = day < first ? 'before' : day > last ? 'after' : '';
            if (hint.dataset.offscreen !== side) {
                hint.dataset.offscreen = side;
            }
        }

        updateHints(first, last);

        // The month label moves over to make room for the chips on the left: as
        // far as they reach into the view, which is what its sticky offset counts from.
        const head = scroller.querySelector('[data-roadmap-head]');
        const before = scroller.querySelector('.roadmap-milestone-hints__before');
        const room = head && before?.offsetWidth
            ? `${Math.ceil(before.getBoundingClientRect().right - scroller.getBoundingClientRect().left - scroller.clientLeft)}px`
            : '';
        if (head && head.style.getPropertyValue('--roadmap-hints-before') !== room) {
            head.style.setProperty('--roadmap-hints-before', room);
        }
    };

    /**
     * On each line, the hint for the nearest bar gone off each side — as long
     * as the bars still in view leave room for it beside them, which is how
     * wide it's allowed to be.
     */
    const updateHints = (first, last) => {
        const left = scroller.scrollLeft;
        const right = left + scroller.clientWidth;
        const inView = new Map();
        for (const entry of entries()) {
            if (endOf(entry) >= first && startOf(entry) <= last) {
                const seen = inView.get(entry.dataset.line);
                inView.set(entry.dataset.line, seen
                    ? [Math.min(seen[0], startOf(entry)), Math.max(seen[1], endOf(entry))]
                    : [startOf(entry), endOf(entry)]);
            }
        }

        for (const line of lines()) {
            const shown = inView.get(line.dataset.roadmapLine);
            const room = {
                before: shown ? shown[0] * state.dayWidth - left - HINT_INSET_PX : Infinity,
                after: shown ? right - (shown[1] + 1) * state.dayWidth - HINT_INSET_PX : Infinity,
            };
            const hints = Array.from(line.querySelectorAll('[data-roadmap-hint]'));
            const nearest = {
                before: hints.filter(h => h.dataset.roadmapHint === 'before' && endOf(h) < first)
                    .reduce((a, h) => !a || endOf(h) > endOf(a) ? h : a, null),
                after: hints.filter(h => h.dataset.roadmapHint === 'after' && startOf(h) > last)
                    .reduce((a, h) => !a || startOf(h) < startOf(a) ? h : a, null),
            };
            for (const hint of hints) {
                const side = hint.dataset.roadmapHint;
                const show = hint === nearest[side] && room[side] >= HINT_MIN_PX;
                hint.toggleAttribute('data-shown', show);
                hint.style.setProperty('--roadmap-hint-room', show && isFinite(room[side]) ? `${room[side]}px` : '');
            }
        }
    };

    const report = () => {
        frame = 0;
        updateOffscreen();
        call('OnViewport', scroller.scrollLeft, scroller.clientWidth);
    };

    const onScroll = () => {
        if (press?.mode === 'marquee') {
            moveMarquee();
        }

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

    /** The line under a point, counting the empty ones below the last as lines too. */
    const lineAtClientY = clientY => Math.max(
        Math.floor((clientY - body().getBoundingClientRect().top) / state.rowHeight), 0);

    /**
     * A point in the body's own coordinates, kept inside it. The body scrolls
     * with the grid, so a point taken here stays on the same day and line
     * however far the view scrolls afterwards.
     */
    const toBody = (clientX, clientY) => {
        const rect = body().getBoundingClientRect();
        return [
            Math.min(Math.max(clientX - rect.left, 0), rect.width),
            Math.min(Math.max(clientY - rect.top, 0), rect.height),
        ];
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

    const onLeave = () => stopTicker();

    // -- Presses -----------------------------------------------------------------

    const onDown = e => {
        if (e.pointerType === 'mouse' && (e.button !== 0 || (IS_MAC && e.ctrlKey))) {
            // A right-click, which the contextmenu event that follows handles.
            return;
        }

        const target = e.target;
        if (target.closest('[data-roadmap-menu], [data-roadmap-hint], [data-roadmap-milestone-hint]')) {
            return;
        }

        // Before the bars: a line drawn over a bar's end is the milestone's to
        // take. Anyone can click one; only a writer's press can become a drag.
        const milestone = target.closest('[data-roadmap-milestone]');
        if (milestone) {
            press = {
                mode: 'milestone-pending',
                pointerId: e.pointerId,
                type: e.pointerType,
                x0: e.clientX, y0: e.clientY, x: e.clientX, y: e.clientY,
                element: milestone,
                milestoneId: milestone.dataset.roadmapMilestone,
                day: Number(milestone.dataset.day),
                hold: null,
                lifted: false,
            };
            if (e.pointerType === 'touch' && state.canWrite) {
                press.hold = setTimeout(() => {
                    if (press) {
                        press.lifted = true;
                        navigator.vibrate?.(10);
                    }
                }, HOLD_MS);
            }
            listen();
            return;
        }

        const bar = target.closest('[data-roadmap-bar]');
        const entry = target.closest('[data-roadmap-entry]');
        const handle = target.closest('[data-roadmap-handle]');

        if (handle && bar && entry && state.canWrite) {
            startResize(e, handle, bar, entry);
            return;
        }

        if (bar && entry) {
            press = {
                mode: 'pending',
                pointerId: e.pointerId,
                type: e.pointerType,
                x0: e.clientX, y0: e.clientY, x: e.clientX, y: e.clientY,
                bar, entry,
                entryId: entry.dataset.roadmapEntry,
                hold: null,
                lifted: false,
                toggle: toggles(e),
                range: e.shiftKey,
            };
            if (e.pointerType === 'touch' && state.canWrite) {
                press.hold = setTimeout(() => press && lift(), HOLD_MS);
            }
            listen();
            return;
        }

        // Anywhere else on the grid: a click there clears the selection, a drag
        // draws a box to select with, and on a touchscreen a long press is the
        // right-click that opens the day's menu.
        if (target.closest('[data-roadmap-body]') && state.canWrite) {
            const [bx, by] = toBody(e.clientX, e.clientY);
            press = {
                mode: 'empty',
                pointerId: e.pointerId,
                type: e.pointerType,
                x0: e.clientX, y0: e.clientY, x: e.clientX, y: e.clientY,
                bx, by,
                additive: toggles(e) || e.shiftKey,
                hold: null,
                held: false,
            };
            if (e.pointerType === 'touch') {
                press.hold = setTimeout(() => {
                    if (press) {
                        press.held = true;
                        navigator.vibrate?.(10);
                    }
                }, HOLD_MS);
            }
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
            case 'milestone-pending':
                if (press.type === 'touch') {
                    if (!press.lifted && travelled > HOLD_SLOP_PX) {
                        abandon(); // a scroll all along
                    } else if (press.lifted && travelled > HOLD_SLOP_PX) {
                        startMilestone();
                    }
                } else if (travelled > MOVE_SLOP_PX && state.canWrite) {
                    startMilestone();
                }
                break;
            case 'milestone':
                moveMilestone();
                break;
            case 'empty':
                if (press.type === 'touch') {
                    if (travelled > HOLD_SLOP_PX) {
                        abandon(); // a scroll all along
                    }
                } else if (travelled > MOVE_SLOP_PX) {
                    startMarquee();
                }
                break;
            case 'marquee':
                moveMarquee();
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
                    ended.entry.classList.remove('is-dragging');
                    await call('OnBarMenu', ended.entryId, ended.x, ended.y);
                } else if (state.canWrite && (ended.toggle || ended.range)) {
                    await call('OnBarSelect', ended.entryId, !ended.toggle);
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
            case 'milestone-pending':
                // Held without moving is a touchscreen's right-click, as on a bar.
                await (ended.lifted
                    ? call('OnMilestoneMenu', ended.milestoneId, ended.x, ended.y)
                    : call('OnMilestoneClick', ended.milestoneId));
                break;
            case 'milestone':
                await endMilestone(ended);
                setGesturing(false);
                break;
            case 'empty':
                // The menu opens on release rather than when the hold is up: open
                // under a finger still down, it would take that finger's lift as
                // a tap outside it and close again.
                await (ended.held
                    ? call('OnDayMenu', dayAtClientX(ended.x), lineAtClientY(ended.y), ended.x, ended.y)
                    : call('OnClearSelection'));
                break;
            case 'marquee':
                await endMarquee(ended);
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
        } else if (press.mode === 'milestone') {
            clearMilestone(press);
        } else if (press.mode === 'marquee') {
            clearMarquee();
        } else if (press.lifted && press.entry) {
            press.entry.classList.remove('is-dragging');
        }

        unlisten();
        press = null;
        setGesturing(false);
    };

    /** Touch only: the hold is up, so this press is the timeline's, not the page's. */
    const lift = () => {
        press.lifted = true;
        press.entry.classList.add('is-dragging');
        navigator.vibrate?.(10);
    };

    /**
     * Refuses the browser's scroll once a finger has taken a bar. Non-passive,
     * and attached only for the length of a press — a permanent one takes every
     * scroll on the page off the fast path.
     */
    const refuseScroll = e => {
        if (press && (press.mode === 'drag' || press.mode === 'move' || press.mode === 'resize' ||
                      press.mode === 'milestone' || press.lifted)) {
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

    // -- Dragging a bar: up and down changes its row, sideways moves its dates ------

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

    const barOf = entry => entry.querySelector('[data-roadmap-bar]');

    /**
     * The bars a drag of this one takes along: every selected bar when it's one
     * of several selected, else itself. .NET marks the selection on the entries
     * (data-selected) and decides the same way when the drop reaches it.
     */
    const groupOf = entry => {
        const selected = entries().filter(e => e.dataset.selected !== undefined);
        return selected.length > 1 && selected.includes(entry) ? selected : [entry];
    };

    // Up or down, the group follows the pointer as it stood, gaps and all. Over
    // the middle of a row they all fit on, the row lights up and they join it;
    // anywhere else a line between rows shows where they'll land, on rows of
    // their own, together and in order.
    const startDrag = () => {
        press.mode = 'drag';
        press.rowOfLine = lines().map(line => Number(line.dataset.row));
        press.group = groupOf(press.entry);
        press.target = null;
        press.scrollTop0 = scroller.scrollTop;
        capture();

        for (const entry of press.group) {
            entry.classList.add('is-dragging');
        }
        moveDrag();
        autoScroll = requestAnimationFrame(scrollNearEdges);
    };

    const moveDrag = () => {
        const dy = press.y - press.y0 + (scroller.scrollTop - press.scrollTop0);
        for (const entry of press.group) {
            entry.style.setProperty('transform', `translateY(${dy}px)`);
        }

        const rect = body().getBoundingClientRect();
        press.target = dropTarget((press.y - rect.top) / state.rowHeight);
        const marker = indicator();
        if (marker) {
            const target = press.target;
            marker.classList.toggle('is-visible', !!target);
            marker.classList.toggle('is-join', !!target?.join);
            if (target) {
                marker.style.top = `${target.line * state.rowHeight}px`;
                marker.style.height = target.join ? `${target.lines * state.rowHeight}px` : '';
            }
        }
    };

    /** Whether the dragged bars all fit on a row beside the bars staying on it, without overlapping one another. */
    const fitsOn = row => {
        const placed = entries().filter(e => Number(e.dataset.row) === row && !press.group.includes(e));
        if (placed.length === 0) {
            return false;
        }

        for (const entry of press.group) {
            if (placed.some(other => overlap(entry, other))) {
                return false;
            }
            placed.push(entry);
        }
        return true;
    };

    /**
     * Where the group lands with the pointer `position` lines down the grid:
     * { join, row, line, lines }, the row to join or the one to land just
     * above (the row count: below the last), and the lines to mark. Null where
     * the drop would leave every bar where it is.
     */
    const dropTarget = position => {
        const rowOfLine = press.rowOfLine;
        const rows = rowOfLine.length ? rowOfLine[rowOfLine.length - 1] + 1 : 0;
        const linesOf = row => rowOfLine.filter(r => r === row).length;
        const rowsHere = new Set(press.group.map(e => Number(e.dataset.row)));
        const fromRow = rowsHere.size === 1 ? [...rowsHere][0] : -1;

        const line = Math.floor(position);
        const within = position - line;
        if (line >= 0 && line < rowOfLine.length && within >= JOIN_FROM && within <= 1 - JOIN_FROM) {
            const row = rowOfLine[line];
            if (fitsOn(row)) {
                return row === fromRow ? null : { join: true, row, line: rowOfLine.indexOf(row), lines: linesOf(row) };
            }
        }

        // The nearest edge between lines, moved down past the rest of a row
        // drawn on more than one: a new row can't go between a row's own lines.
        let edge = Math.min(Math.max(Math.round(position), 0), rowOfLine.length);
        while (edge > 0 && edge < rowOfLine.length && rowOfLine[edge] === rowOfLine[edge - 1]) {
            edge++;
        }

        const row = edge < rowOfLine.length ? rowOfLine[edge] : rows;
        const wholeRow = fromRow >= 0 && entries().every(e => Number(e.dataset.row) !== fromRow || press.group.includes(e));
        return wholeRow && (row === fromRow || row === fromRow + 1)
            ? null
            : { join: false, row, line: edge, lines: 0 };
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

    // Sideways, the whole group moves by the same number of days, so the
    // window's edge stops it where its first bar meets the edge.
    const startMove = () => {
        press.mode = 'move';
        press.group = groupOf(press.entry);
        press.start = startOf(press.entry);
        press.end = endOf(press.entry);
        press.first = Math.min(...press.group.map(startOf));
        press.last = Math.max(...press.group.map(endOf));
        press.scrollLeft0 = scroller.scrollLeft;
        press.shift = 0;
        capture();

        for (const entry of press.group) {
            entry.classList.add('is-dragging');
        }
        label = document.createElement('div');
        label.className = 'roadmap-resize-label';
        document.body.appendChild(label);
        moveShift();
        autoScroll = requestAnimationFrame(scrollNearSides);
    };

    /**
     * Snaps to whole days, and stops at the window's edges. The preview offset
     * goes on the bar, not its entry: when .NET redraws the bar in its new place
     * it rewrites the bar's style, which drops the offset in the same update — on
     * the entry it would outlive the redraw for a frame and add to the new place.
     * Each bar is looked up afresh, since scrolling sideways can draw one of the
     * group's bars that wasn't drawn when the drag began.
     */
    const moveShift = () => {
        const dx = press.x - press.x0 + (scroller.scrollLeft - press.scrollLeft0);
        press.shift = Math.min(Math.max(Math.round(dx / state.dayWidth), -press.first), state.days - 1 - press.last);
        for (const entry of press.group) {
            barOf(entry)?.style.setProperty('transform', `translateX(${press.shift * state.dayWidth}px)`);
        }

        const from = press.start + press.shift;
        const to = press.end + press.shift;
        const barRect = press.bar.getBoundingClientRect();
        label.textContent = from === to ? formatShort(from) : `${formatShort(from)} – ${formatShort(to)}`;
        label.style.left = `${barRect.left}px`;
        label.style.top = `${barRect.top}px`;
    };

    /** Scrolls sideways while a bar or a milestone is held near either side. */
    const scrollNearSides = () => {
        const mover = press?.mode === 'move' ? moveShift : press?.mode === 'milestone' ? moveMilestone : null;
        if (!mover) {
            return;
        }

        const rect = scroller.getBoundingClientRect();
        if (press.x < rect.left + EDGE_PX) {
            scroller.scrollLeft -= EDGE_SPEED_PX;
            mover();
        } else if (press.x > rect.right - EDGE_PX) {
            scroller.scrollLeft += EDGE_SPEED_PX;
            mover();
        }

        autoScroll = requestAnimationFrame(scrollNearSides);
    };

    const clearMove = ended => {
        for (const entry of ended.group) {
            barOf(entry)?.style.removeProperty('transform');
            entry.classList.remove('is-dragging');
        }
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
        for (const entry of ended.group) {
            entry.style.removeProperty('transform');
            entry.classList.remove('is-dragging');
        }
        indicator()?.classList.remove('is-visible', 'is-join');
    };

    const endDrag = async ended => {
        try {
            if (ended.target) {
                // .NET redraws the bars on their new lines before this returns,
                // so dropping the offset doesn't flash them back where they were.
                await call('OnRowDrop', ended.entryId, ended.target.row, ended.target.join);
            }
        } finally {
            clearDrag(ended);
        }
    };

    // -- Moving a milestone ---------------------------------------------------------

    // Its flag and head line are in the sticky header and its line is in the
    // body, so all three move together, found afresh by id.
    const milestoneParts = id => scroller.querySelectorAll(`[data-roadmap-milestone="${id}"]`);

    const startMilestone = () => {
        press.mode = 'milestone';
        press.scrollLeft0 = scroller.scrollLeft;
        press.shift = 0;
        stopTicker();
        setGesturing(true);
        try {
            press.element.setPointerCapture(press.pointerId);
        } catch {
            // Window listeners cover it.
        }

        for (const part of milestoneParts(press.milestoneId)) {
            part.classList.add('is-dragging');
        }
        label = document.createElement('div');
        label.className = 'roadmap-resize-label';
        document.body.appendChild(label);
        moveMilestone();
        autoScroll = requestAnimationFrame(scrollNearSides);
    };

    /**
     * Whole days, within the window. As with a bar, the preview is a transform
     * that .NET's redraw of the new place replaces with its own style.
     */
    const moveMilestone = () => {
        const dx = press.x - press.x0 + (scroller.scrollLeft - press.scrollLeft0);
        press.shift = Math.min(Math.max(Math.round(dx / state.dayWidth), -press.day), state.days - 1 - press.day);
        for (const part of milestoneParts(press.milestoneId)) {
            part.style.setProperty('transform', `translateX(${press.shift * state.dayWidth}px)`);
        }

        const flag = scroller.querySelector(`[data-roadmap-milestone="${press.milestoneId}"] .roadmap-milestone__flag`);
        const rect = (flag ?? press.element).getBoundingClientRect();
        label.textContent = formatDay(press.day + press.shift);
        label.style.left = `${rect.left}px`;
        label.style.top = `${rect.top}px`;
    };

    const clearMilestone = ended => {
        for (const part of milestoneParts(ended.milestoneId)) {
            part.style.removeProperty('transform');
            part.classList.remove('is-dragging');
        }
        label?.remove();
        label = null;
    };

    const endMilestone = async ended => {
        try {
            if (ended.shift !== 0) {
                await call('OnMilestoneMove', ended.milestoneId, ended.day + ended.shift);
            }
        } finally {
            clearMilestone(ended);
        }
    };

    // -- Resizing bars -------------------------------------------------------------

    const startResize = (e, handle, bar, entry) => {
        e.preventDefault();
        press = {
            mode: 'resize',
            pointerId: e.pointerId,
            type: e.pointerType,
            x0: e.clientX, y0: e.clientY, x: e.clientX, y: e.clientY,
            edge: handle.dataset.roadmapHandle,
            bar, entry,
            entryId: entry.dataset.roadmapEntry,
            start: startOf(entry),
            end: endOf(entry),
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

    // -- Selecting with a box -------------------------------------------------------

    const marquee = () => scroller.querySelector('[data-roadmap-marquee]');

    const startMarquee = () => {
        press.mode = 'marquee';
        stopTicker();
        try {
            scroller.setPointerCapture(press.pointerId);
        } catch {
            // Window listeners cover it.
        }

        moveMarquee();
        autoScroll = requestAnimationFrame(scrollMarquee);
    };

    /**
     * Draws the box from where the press started to the pointer, and marks the
     * bars it touches. Hits are worked out from each entry's line and columns
     * rather than its bar, so a bar scrolled out of view, and so not drawn, is
     * still caught. The marks are the browser's until the box is let go; .NET
     * owns the selection itself.
     */
    const moveMarquee = () => {
        const [x, y] = toBody(press.x, press.y);
        const left = Math.min(press.bx, x);
        const right = Math.max(press.bx, x);
        const top = Math.min(press.by, y);
        const bottom = Math.max(press.by, y);

        const box = marquee();
        if (box) {
            box.style.left = `${left}px`;
            box.style.top = `${top}px`;
            box.style.width = `${right - left}px`;
            box.style.height = `${bottom - top}px`;
            box.classList.add('is-visible');
        }

        press.hits = [];
        for (const entry of entries()) {
            const lineTop = Number(entry.dataset.line) * state.rowHeight;
            const barLeft = startOf(entry) * state.dayWidth;
            const barRight = (endOf(entry) + 1) * state.dayWidth;
            const hit = left < barRight && right > barLeft && top < lineTop + state.rowHeight && bottom > lineTop;
            entry.classList.toggle('is-marked', hit);
            if (hit) {
                press.hits.push(entry.dataset.roadmapEntry);
            }
        }
    };

    const scrollMarquee = () => {
        if (!press || press.mode !== 'marquee') {
            return;
        }

        const rect = scroller.getBoundingClientRect();
        const headHeight = scroller.querySelector('[data-roadmap-head]')?.offsetHeight ?? 0;
        const dx = press.x < rect.left + EDGE_PX ? -EDGE_SPEED_PX : press.x > rect.right - EDGE_PX ? EDGE_SPEED_PX : 0;
        const dy = press.y < rect.top + headHeight + EDGE_PX ? -EDGE_SPEED_PX
            : press.y > rect.bottom - EDGE_PX ? EDGE_SPEED_PX : 0;
        if (dx || dy) {
            // The scroll event this raises redraws the box.
            scroller.scrollLeft += dx;
            scroller.scrollTop += dy;
        }

        autoScroll = requestAnimationFrame(scrollMarquee);
    };

    const clearMarquee = () => {
        marquee()?.classList.remove('is-visible');
        for (const entry of entries()) {
            entry.classList.remove('is-marked');
        }
    };

    const endMarquee = async ended => {
        try {
            // As with a move, .NET draws the selection before this returns, so
            // the marks come off without a frame of neither.
            await call('OnMarquee', ended.hits ?? [], ended.additive);
        } finally {
            clearMarquee();
        }
    };

    // -- Menus and hints ---------------------------------------------------------

    // A bar's menu, or the menu for the day under the pointer anywhere else on
    // the grid. Both are drawn by the host, natively in the desktop app.
    const onContextMenu = e => {
        const target = e.target;
        if (!(target instanceof Element) ||
            target.closest('[data-roadmap-hint], [data-roadmap-menu], [data-roadmap-milestone-hint]')) {
            return;
        }

        const milestone = target.closest('[data-roadmap-milestone]');
        if (milestone) {
            if (!state.canWrite) {
                return;
            }

            e.preventDefault();
            if (e.pointerType !== 'touch' && !(press && press.type === 'touch')) {
                abandon();
                call('OnMilestoneMenu', milestone.dataset.roadmapMilestone, e.clientX, e.clientY);
            }
            return;
        }

        const entry = target.closest('[data-roadmap-entry]');
        const onBar = !!entry && !!target.closest('[data-roadmap-bar]');
        if (!onBar && !(state.canWrite && target.closest('[data-roadmap-body]'))) {
            return;
        }

        e.preventDefault();
        // On a touchscreen the long press already did this.
        if (e.pointerType === 'touch' || (press && press.type === 'touch')) {
            return;
        }

        abandon();
        if (onBar) {
            call('OnBarMenu', entry.dataset.roadmapEntry, e.clientX, e.clientY);
        } else {
            call('OnDayMenu', dayAtClientX(e.clientX), lineAtClientY(e.clientY), e.clientX, e.clientY);
        }
    };

    // Escape lets go of the selection, or of a gesture under way; Delete takes
    // the selection off the roadmap. On a Mac that's Cmd-Delete, as Move to
    // Trash is in the Finder, so a stray press of the key beside Return doesn't.
    // Only when the key is the grid's to take: with focus in a menu or a field
    // it's theirs, and with anything open over the page (the task drawer, a
    // dialog) it's for that. Those often leave focus on the bar that opened
    // them, so the overlay they draw is what tells.
    const onKeyDown = e => {
        const remove = e.key === 'Delete' || (IS_MAC && e.metaKey && e.key === 'Backspace');
        if ((e.key !== 'Escape' && !remove) || e.defaultPrevented ||
            (e.target !== document.body && !(e.target instanceof Node && scroller.contains(e.target))) ||
            document.querySelector('.mud-overlay')) {
            return;
        }

        if (remove) {
            if (state.canWrite && !press) {
                e.preventDefault();
                call('OnRemoveSelected');
            }
            return;
        }

        if (press) {
            abandon();
        } else {
            call('OnClearSelection');
        }
    };

    const onClick = e => {
        // A milestone's chip brings its line to the middle of the view.
        const chip = e.target.closest('[data-roadmap-milestone-hint]');
        if (chip) {
            const x = (Number(chip.dataset.day) + 1) * state.dayWidth;
            scroller.scrollTo({ left: Math.max(x - scroller.clientWidth / 2, 0), behavior: 'smooth' });
            return;
        }

        const hint = e.target.closest('[data-roadmap-hint]');
        if (!hint) {
            return;
        }

        scroller.scrollTo({ left: Math.max(startOf(hint) - 2, 0) * state.dayWidth, behavior: 'smooth' });
    };

    // WebKit runs a long-press drag of its own on anything it thinks is draggable.
    const onDragStart = e => {
        if (e.target instanceof Element && e.target.closest('[data-roadmap-bar], [data-roadmap-milestone]')) {
            e.preventDefault();
        }
    };

    scroller.addEventListener('pointerdown', onDown);
    scroller.addEventListener('pointerover', onOver);
    scroller.addEventListener('pointerleave', onLeave);
    scroller.addEventListener('contextmenu', onContextMenu);
    scroller.addEventListener('click', onClick);
    scroller.addEventListener('dragstart', onDragStart);
    document.addEventListener('keydown', onKeyDown);

    report();

    const handle = {
        scrollToDay: index => {
            scroller.scrollLeft = Math.max(index, 0) * state.dayWidth;
            report();
        },
        /** Scrolls the least distance that shows the line whole, below the sticky day header. */
        revealLine: index => {
            const head = scroller.querySelector('[data-roadmap-head]')?.offsetHeight ?? 0;
            const top = index * state.rowHeight;
            if (top < scroller.scrollTop) {
                scroller.scrollTop = top;
            } else if (head + top + state.rowHeight > scroller.scrollTop + scroller.clientHeight) {
                scroller.scrollTop = head + top + state.rowHeight - scroller.clientHeight;
            }
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
            scroller.removeEventListener('pointerover', onOver);
            scroller.removeEventListener('pointerleave', onLeave);
            scroller.removeEventListener('contextmenu', onContextMenu);
            scroller.removeEventListener('click', onClick);
            scroller.removeEventListener('dragstart', onDragStart);
            document.removeEventListener('keydown', onKeyDown);
            if (attached.get(scroller) === handle) {
                attached.delete(scroller);
            }
        },
    };
    attached.set(scroller, handle);
    return handle;
}
