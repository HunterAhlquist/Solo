// ---------------------------------------------------------------------------
// Dragging.
//
// One engine, driven by Pointer Events, for every drag in the app: cards
// between columns, epic bands, sub-task rows, projects in the sidebar. A mouse,
// a trackpad, a finger and a pencil all arrive as the same pointerdown /
// pointermove / pointerup, so there is one code path and not two that have to
// agree.
//
// HTML5 drag & drop is deliberately not used. On iPadOS and iOS a drag from a
// finger is a *platform* gesture (UIDragInteraction), not a web one: it lifts on
// its own schedule, its preview is clipped to whatever scroller the element sits
// in, it fights the text-selection callout for the same press, and it will not
// scroll the page to reach something further down. Feature-detecting around that
// meant asking "is the primary pointer coarse?", which iPadOS answers "yes" even
// with a Magic Keyboard trackpad attached — so the mouse lost drag & drop too.
// Pointer Events have none of those problems and no such question to ask.
//
// The DOM contract, all data attributes, so markup declares its own behaviour:
//
//   data-drag-zone="<id>"      a container that can receive a drop
//   data-drag-group="<name>"   on the zone; only items of the same group land here
//   data-drag-item="<id>"      a draggable child of a zone
//   data-drag-from="<sel>"     on the item: a drag may only begin inside this
//   data-drag-handle           on a descendant: begins the drag *immediately*
//   data-drag-preview="<sel>"  on the item: clone this instead of the whole item
//   data-drag-outline          on the zone: paint it as a target while dragging
//   data-drag-empty-after      on a zone's direct child: empty drops land below it
//
// Nothing here knows what a task or an epic is. A drop is reported to Blazor as
// (group, itemId, zoneId, index) — index being the slot among the zone's *other*
// items, which is exactly what a fractional rank needs.
// ---------------------------------------------------------------------------

window.solo = window.solo || {};

(() => {
    const ZONE = 'data-drag-zone';
    const GROUP = 'data-drag-group';
    const ITEM = 'data-drag-item';
    const HANDLE = 'data-drag-handle';

    // A drag from a handle starts the instant the pointer goes down — a handle
    // is there for nothing else. Anywhere else has to be told apart from the
    // gesture it shares the surface with: a click, or on a touchscreen a scroll.
    // A fine pointer can say so by moving, which is instant enough to feel
    // direct; a finger has to hold still, and a third of a second is about the
    // shortest hold that isn't triggered by the wobble in a tap.
    const MOVE_SLOP_PX = 6;
    const HOLD_MS = 320;
    const HOLD_SLOP_PX = 10;

    // Dragging near the edge of a scroller scrolls it, faster the further in you
    // push, up to a frame's worth. The band is a share of the scroller's own
    // height so a short one (a drawer's checklist) doesn't become all band.
    const SCROLL_BAND_RATIO = 0.16;
    const SCROLL_BAND_MIN_PX = 48;
    const SCROLL_BAND_MAX_PX = 110;
    const SCROLL_MAX_PX = 16;

    const CONTROLS = 'button, a, input, textarea, select, [role="button"]';

    /** Handlers the board module hangs off the drag's lifecycle. */
    const listeners = { start: [], move: [], end: [] };
    const emit = (type, drag) => listeners[type].forEach(fn => fn(drag));

    let dotnet = null;

    // -- The overlay --------------------------------------------------------
    //
    // Everything the drag draws — the card under the pointer, the insertion
    // line, the target outlines — lives in one fixed-position layer that is a
    // direct child of <body>. That is the whole answer to a preview that used to
    // slide under other cards or get masked at the edge of an epic: no scroller,
    // no stacking context and no overflow:hidden in the page can reach it.

    let layer = null;

    const overlay = () => {
        if (!layer || !layer.isConnected) {
            layer = document.createElement('div');
            layer.className = 'solo-drag-layer';
            layer.setAttribute('aria-hidden', 'true');
            document.body.appendChild(layer);
        }

        return layer;
    };

    // -- Finding things -----------------------------------------------------

    const zonesOf = group =>
        [...document.querySelectorAll(`[${ZONE}][${GROUP}="${CSS.escape(group)}"]`)];

    /**
     * The zone's own items. A zone can contain zones of another group — the
     * board's epic list holds every band, and each band holds three card
     * columns — so an element counts here only if this is the nearest zone
     * above it.
     */
    const itemsOf = zone =>
        [...zone.querySelectorAll(`[${ITEM}]`)].filter(el => el.closest(`[${ZONE}]`) === zone);

    const rectOf = el => el.getBoundingClientRect();

    const intersect = (a, b) => ({
        left: Math.max(a.left, b.left),
        top: Math.max(a.top, b.top),
        right: Math.min(a.right, b.right),
        bottom: Math.min(a.bottom, b.bottom),
    });

    const empty = box => box.right <= box.left || box.bottom <= box.top;

    /**
     * How much of an element is actually on show: its box, cut down by every
     * ancestor that clips and by the viewport. The overlay draws at the top of
     * the document and so respects none of that on its own — without this, a
     * card column scrolled off the side of the phone carousel would have its
     * outline painted straight across the screen, and could be dropped into
     * while invisible.
     */
    const visibleRect = element => {
        let box = rectOf(element);
        box = { left: box.left, top: box.top, right: box.right, bottom: box.bottom };

        for (let node = element.parentElement; node && node !== document.body; node = node.parentElement) {
            const style = getComputedStyle(node);
            if (style.overflowX !== 'visible' || style.overflowY !== 'visible') {
                box = intersect(box, rectOf(node));
                if (empty(box)) {
                    return box;
                }
            }
        }

        return intersect(box, { left: 0, top: 0, right: window.innerWidth, bottom: window.innerHeight });
    };

    /**
     * The nearest ancestor that can scroll the way we are asking, falling back
     * to the document. Asking about the direction as well as the overflow style
     * is what stops a checklist already at its end swallowing the scroll instead
     * of handing it to the page behind it — and what passes over a band's column
     * strip, which overflows sideways and is therefore technically a vertical
     * scroller too.
     */
    const verticalScroller = (element, by) => {
        for (let node = element; node && node !== document.body; node = node.parentElement) {
            const room = by < 0
                ? node.scrollTop > 0
                : node.scrollTop + node.clientHeight < node.scrollHeight - 1;
            if (room && /(auto|scroll|overlay)/.test(getComputedStyle(node).overflowY)) {
                return node;
            }
        }

        return document.scrollingElement ?? document.documentElement;
    };

    // -- State --------------------------------------------------------------

    let pending = null;  // pointer is down on something draggable; not yet a drag
    let drag = null;     // the live drag
    let frame = null;
    let zoneRects = null;

    const invalidate = () => { zoneRects = null; };

    const measure = () => {
        if (!zoneRects) {
            zoneRects = drag.zones
                .map(zone => ({ zone, box: visibleRect(zone) }))
                .filter(entry => !empty(entry.box));
        }

        return zoneRects;
    };

    // -- Picking a target ---------------------------------------------------

    /**
     * The zone under the pointer — the innermost, so a zone nested in another
     * wins. Failing that the nearest one within a card's reach, which is what
     * makes the gap between two columns land somewhere rather than nowhere.
     */
    const zoneAt = (x, y) => {
        let inside = null;
        let insideArea = Infinity;
        let nearest = null;
        let nearestDistance = Infinity;

        for (const entry of measure()) {
            const { box } = entry;
            if (x >= box.left && x <= box.right && y >= box.top && y <= box.bottom) {
                const area = (box.right - box.left) * (box.bottom - box.top);
                if (area < insideArea) {
                    inside = entry;
                    insideArea = area;
                }

                continue;
            }

            const dx = Math.max(box.left - x, 0, x - box.right);
            const dy = Math.max(box.top - y, 0, y - box.bottom);
            const distance = Math.hypot(dx, dy);
            if (distance < nearestDistance) {
                nearestDistance = distance;
                nearest = entry;
            }
        }

        return inside ?? (nearestDistance <= 40 ? nearest : null);
    };

    /**
     * Where in the zone the pointer is asking to insert, counted among the
     * zone's *other* items: the number of them whose middle it has passed. The
     * same number a fractional rank wants, so nothing downstream has to
     * compensate for the item having been lifted out of its own list.
     */
    const slotIn = (zone, y) => {
        const others = itemsOf(zone).filter(item => item !== drag.item);
        let index = 0;
        // Headers remain visible in empty or collapsed containers. The first
        // item belongs below that header, rather than at the zone's top edge.
        const emptyAfter = zone.querySelector(':scope > [data-drag-empty-after]');
        let line = emptyAfter ? rectOf(emptyAfter).bottom + 6 : null;

        for (const item of others) {
            const box = rectOf(item);
            if (y < box.top + box.height / 2) {
                line = box.top;
                break;
            }

            index += 1;
            line = box.bottom;
        }

        return { index, line };
    };

    // -- Painting -----------------------------------------------------------

    const paintTarget = (target, slot) => {
        const boxes = new Map(measure().map(entry => [entry.zone, entry.box]));

        for (const outline of drag.outlines) {
            const box = boxes.get(outline.zone);
            outline.element.style.opacity = box ? '1' : '0';
            outline.element.classList.toggle('solo-drag-target--active', outline.zone === target?.zone);
            if (box) {
                outline.element.style.transform = `translate(${box.left}px, ${box.top}px)`;
                outline.element.style.width = `${box.right - box.left}px`;
                outline.element.style.height = `${box.bottom - box.top}px`;
            }
        }

        if (!target) {
            drag.line.style.opacity = '0';
            return;
        }

        const { box } = target;

        // An empty zone has no item to sit beside, so the line goes just inside
        // its top edge — where the first card would land.
        const at = Math.min(Math.max(slot.line ?? box.top + 6, box.top + 1), box.bottom - 1);
        drag.line.style.opacity = '1';
        drag.line.style.transform = `translate(${box.left + 6}px, ${Math.round(at) - 1}px)`;
        drag.line.style.width = `${Math.max(box.right - box.left - 12, 0)}px`;
    };

    const paintGhost = () => {
        drag.ghost.style.transform =
            `translate3d(${Math.round(drag.x - drag.grabX)}px, ${Math.round(drag.y - drag.grabY)}px, 0)`;
    };

    // -- Auto-scroll --------------------------------------------------------

    /**
     * How far to scroll this frame, and what. Measured against the scroller's own
     * box rather than the screen's, so the checklist inside the task drawer
     * scrolls when the pointer nears the bottom of the drawer — not the bottom
     * of the phone.
     */
    const scrollStep = () => {
        const element = document.elementFromPoint(
            Math.max(0, Math.min(drag.x, window.innerWidth - 1)),
            Math.max(0, Math.min(drag.y, window.innerHeight - 1))) ?? document.body;

        const viewport = { top: 0, bottom: window.innerHeight, height: window.innerHeight };
        const near = (box, height) => {
            const band = Math.min(SCROLL_BAND_MAX_PX, Math.max(SCROLL_BAND_MIN_PX, height * SCROLL_BAND_RATIO));
            return drag.y < box.top + band ? (drag.y - (box.top + band)) / band
                : drag.y > box.bottom - band ? (drag.y - (box.bottom - band)) / band
                : 0;
        };

        // The direction has to be known before the scroller is, since which
        // scroller answers depends on which way it still has room to go.
        const past = near(viewport, viewport.height);
        if (past === 0) {
            return null;
        }

        const scroller = verticalScroller(element, past);
        const page = scroller === document.scrollingElement || scroller === document.documentElement;
        const box = page ? viewport : rectOf(scroller);
        const depth = page ? past : near(box, box.height);
        if (depth === 0) {
            return null;
        }

        // Squared, so the first few pixels into the band creep and the last few
        // race — a linear ramp reads as "it ran away with the card".
        const ramp = Math.min(Math.abs(depth), 1) ** 2;
        return { scroller, page, by: Math.sign(depth) * Math.max(1, Math.round(ramp * SCROLL_MAX_PX)) };
    };

    // -- The loop -----------------------------------------------------------

    const tick = () => {
        frame = requestAnimationFrame(tick);
        if (!drag) {
            return;
        }

        // Everything the board is asked, before anything on screen moves: a
        // frame that interleaved the two would pay for a fresh layout between
        // every question and the next.
        const target = zoneAt(drag.x, drag.y);
        const slot = target ? slotIn(target.zone, drag.y) : null;
        const scroll = scrollStep();

        drag.zone = target?.zone ?? null;
        drag.index = slot?.index ?? 0;

        paintGhost();
        paintTarget(target, slot);
        emit('move', drag);

        if (scroll) {
            if (scroll.page) {
                window.scrollBy(0, scroll.by);
            } else {
                scroll.scroller.scrollTop += scroll.by;
            }
        }
    };

    // -- Lifting and landing ------------------------------------------------

    /**
     * The element the press landed inside that matches the item's data-drag-from
     * selector — and null if it landed outside it, which is how a card inside an
     * epic band is kept from dragging the band. No selector means the whole item.
     */
    const within = (target, item, selector) => {
        if (!selector) {
            return item;
        }

        let match = null;
        try {
            match = target.closest(selector);
        } catch {
            return null;
        }

        return match && item.contains(match) ? match : null;
    };

    const previewOf = item => {
        const selector = item.getAttribute('data-drag-preview');
        if (!selector) {
            return item;
        }

        try {
            return item.querySelector(selector) ?? item;
        } catch {
            return item;
        }
    };

    const lift = () => {
        const { item, group, x, y } = pending;
        const preview = previewOf(item);
        const box = rectOf(preview);

        const ghost = document.createElement('div');
        ghost.className = 'solo-drag-ghost';
        ghost.style.width = `${box.width}px`;
        ghost.style.height = `${box.height}px`;

        const clone = preview.cloneNode(true);
        clone.removeAttribute('id');
        clone.querySelectorAll('[id]').forEach(el => el.removeAttribute('id'));
        ghost.appendChild(clone);

        const line = document.createElement('div');
        line.className = 'solo-drag-line';

        // A group can only move among the board's top-level items. Its own
        // nested zone and descendants must never receive the lifted group.
        const sourceZone = item.parentElement.closest(`[${ZONE}]`);
        const zones = zonesOf(group).filter(zone => !item.contains(zone) &&
            (!item.hasAttribute('data-drag-root-only') || zone === sourceZone));
        const outlines = zones
            .filter(candidate => candidate.hasAttribute('data-drag-outline'))
            .map(candidate => {
                const element = document.createElement('div');
                element.className = 'solo-drag-target';
                if (candidate.getAttribute('data-drag-outline') === 'group') {
                    element.classList.add('solo-drag-target--group');
                    if (candidate === sourceZone) element.classList.add('solo-drag-target--source-group');
                }
                return { zone: candidate, element };
            });

        const canvas = overlay();
        outlines.forEach(outline => canvas.appendChild(outline.element));
        canvas.appendChild(line);
        canvas.appendChild(ghost);

        drag = {
            pointerId: pending.pointerId,
            group,
            item,
            itemId: item.getAttribute(ITEM),
            zones,
            outlines,
            ghost,
            line,
            // Where inside the preview the pointer took hold. Keeping that offset
            // constant is what glues the card to the pointer no matter how far
            // the page scrolls underneath it.
            grabX: x - box.left,
            grabY: y - box.top,
            x,
            y,
            zone: null,
            index: 0,
        };

        pending = null;
        item.classList.add('solo-drag-source');
        document.body.classList.add('solo-dragging');
        navigator.vibrate?.(8);

        window.addEventListener('scroll', invalidate, true);
        window.addEventListener('resize', invalidate);
        invalidate();

        emit('start', drag);
        paintGhost();
        frame = requestAnimationFrame(tick);
    };

    const teardown = () => {
        cancelAnimationFrame(frame);
        frame = null;
        window.removeEventListener('scroll', invalidate, true);
        window.removeEventListener('resize', invalidate);
        zoneRects = null;

        drag.item.classList.remove('solo-drag-source');
        document.body.classList.remove('solo-dragging');
        drag.ghost.remove();
        drag.line.remove();
        drag.outlines.forEach(outline => outline.element.remove());
        emit('end', drag);
        drag = null;
    };

    /**
     * A pointerup that ends a drag is still followed by a click, and the things
     * that drag are usually things that also open: a card opens its drawer, an
     * epic band opens the epic. Eat exactly one.
     */
    const swallowNextClick = () => {
        const eat = e => {
            e.stopPropagation();
            e.preventDefault();
        };

        window.addEventListener('click', eat, { capture: true, once: true });
        setTimeout(() => window.removeEventListener('click', eat, true), 400);
    };

    const drop = () => {
        const landed = drag.zone
            ? { group: drag.group, itemId: drag.itemId, zoneId: drag.zone.getAttribute(ZONE), index: drag.index }
            : null;

        teardown();
        swallowNextClick();

        if (landed && dotnet) {
            dotnet.invokeMethodAsync('OnDropAsync', landed);
        }
    };

    const cancel = () => {
        teardown();
        swallowNextClick();
    };

    // -- The gesture --------------------------------------------------------

    const abandon = () => {
        if (pending) {
            clearTimeout(pending.hold);
            pending = null;
            detach();
        }
    };

    const onPointerDown = e => {
        if (drag || pending || !e.isPrimary || (e.pointerType === 'mouse' && e.button !== 0)) {
            return;
        }

        const target = e.target instanceof Element ? e.target : null;
        const item = target?.closest(`[${ITEM}]`);
        const group = item?.closest(`[${ZONE}]`)?.getAttribute(GROUP);
        if (!item || !group) {
            return;
        }

        // Where a drag may begin inside this item. An epic band drags from its
        // gutter but not from its cards; a sub-task row drags only from its
        // handle; a card drags from anywhere on itself.
        const from = within(target, item, item.getAttribute('data-drag-from'));
        if (!from) {
            return;
        }

        const handle = target.closest(`[${HANDLE}]`);
        const grabbedHandle = Boolean(handle && item.contains(handle));

        // A control inside the drag source is a control first — the epic band's
        // gutter is a drag source and also holds its collapse, add and delete
        // buttons. The source itself may well be one too: the gutter is a button,
        // since pressing it opens the epic.
        const control = target.closest(CONTROLS);
        if (!grabbedHandle && control && control !== from && from.contains(control)) {
            return;
        }

        pending = {
            pointerId: e.pointerId,
            pointerType: e.pointerType,
            item,
            group,
            startX: e.clientX,
            startY: e.clientY,
            x: e.clientX,
            y: e.clientY,
            hold: null,
        };

        attach();

        if (grabbedHandle) {
            lift();
        } else if (e.pointerType === 'touch') {
            pending.hold = setTimeout(() => pending && lift(), HOLD_MS);
        }
    };

    const onPointerMove = e => {
        if (drag) {
            if (e.pointerId !== drag.pointerId) {
                return;
            }

            drag.x = e.clientX;
            drag.y = e.clientY;
            return;
        }

        if (!pending || e.pointerId !== pending.pointerId) {
            return;
        }

        pending.x = e.clientX;
        pending.y = e.clientY;
        const travelled = Math.hypot(e.clientX - pending.startX, e.clientY - pending.startY);

        if (pending.pointerType === 'touch') {
            // Wandered before the hold was up: this was a scroll all along, and
            // it stays one — nothing here has taken the gesture.
            if (travelled > HOLD_SLOP_PX) {
                abandon();
            }
        } else if (travelled > MOVE_SLOP_PX) {
            lift();
        }
    };

    const onPointerUp = e => {
        if (drag && e.pointerId === drag.pointerId) {
            drag.x = e.clientX;
            drag.y = e.clientY;

            // Land on where the pointer actually left, which is not always where
            // it was last seen moving — and measure the board fresh: a release
            // during the carousel's magnetic scroll would otherwise be judged
            // against where the columns were on the way past.
            invalidate();
            const target = zoneAt(drag.x, drag.y);
            drag.zone = target?.zone ?? null;
            if (target) {
                drag.index = slotIn(target.zone, drag.y).index;
            }

            detach();
            drop();
            return;
        }

        abandon();
    };

    const onPointerCancel = e => {
        if (drag && e.pointerId === drag.pointerId) {
            detach();
            cancel();
            return;
        }

        abandon();
    };

    /**
     * Taking the gesture from the browser.
     *
     * touch-action alone would do it, but only where it can be set in advance —
     * on a handle, which exists for nothing else. A card and an epic band are
     * also the surface you scroll the board with, so they keep touch-action and
     * the scroll is refused here instead, once the hold has decided this is a
     * drag. That refusal is honoured because the finger was still while it was
     * being decided: no scroll has begun for the browser to have committed to.
     *
     * Non-passive, and attached only for the length of a press that landed on
     * something draggable: a permanent non-passive touchmove on the document
     * takes every scroll on every page off the browser's fast path.
     */
    const refuseScroll = e => {
        if (drag) {
            e.preventDefault();
        }
    };

    const attach = () => {
        document.addEventListener('touchmove', refuseScroll, { passive: false });
        window.addEventListener('pointermove', onPointerMove);
        window.addEventListener('pointerup', onPointerUp);
        window.addEventListener('pointercancel', onPointerCancel);
    };

    const detach = () => {
        document.removeEventListener('touchmove', refuseScroll, { passive: false });
        window.removeEventListener('pointermove', onPointerMove);
        window.removeEventListener('pointerup', onPointerUp);
        window.removeEventListener('pointercancel', onPointerCancel);
    };

    document.addEventListener('pointerdown', onPointerDown, true);

    // A long press on a touchscreen is a drag here, not a right click, but
    // Android fires contextmenu on the same press — which would open a card's
    // task menu over the card being lifted. Swallowed before the page sees it.
    document.addEventListener('contextmenu', e => {
        if (drag || pending?.pointerType === 'touch') {
            e.preventDefault();
            e.stopPropagation();
        }
    }, true);

    // WebKit still runs a long-press drag of its own on anything it thinks is
    // draggable, and it would swallow the touch stream mid-press. Nothing in the
    // app sets the attribute any more; this is the guard for anything MudBlazor
    // or an extension might.
    document.addEventListener('dragstart', e => {
        if (e.target instanceof Element && e.target.closest(`[${ITEM}]`)) {
            e.preventDefault();
        }
    }, true);

    window.solo.drag = {
        init: reference => { dotnet = reference; },
        on: (type, handler) => listeners[type].push(handler),
    };
})();

// ---------------------------------------------------------------------------
// Dragging a card to a column that is off-screen (the phone carousel).
//
// Below the md breakpoint each band's three status columns become a horizontal
// scroller, so the column you mean to drop into is usually not on screen when
// you pick a card up. Holding the card against either edge of the screen lights
// that edge, and after a dwell the band slides one column that way — then goes
// quiet for the same length of time again, so a finger resting near an edge on
// its way somewhere else doesn't page through the whole board.
//
// The edges are signage, not drop zones: they never take the drop themselves,
// so once the column has arrived you drag back into the middle and let go. The
// two indicator elements belong to Board.razor; this only toggles classes on
// them. Inert everywhere but the carousel, since it only arms over a band that
// has somewhere left to scroll — which the desktop grid never has.
// ---------------------------------------------------------------------------
(() => {
    const EDGE_WIDTH_PX = 44;
    const DWELL_MS = 1500;
    const COOLDOWN_MS = 1500;

    let side = null;
    let dwell = null;
    let cooldown = null;
    let pointerY = 0;

    const indicator = () => (side ? document.querySelector(`.board-edge--${side}`) : null);

    /**
     * Three states, and each on its own class: lit at all, charging towards a
     * step, settling after one. Kept apart so restarting the charge animation —
     * which takes removing the class and forcing a reflow — doesn't also restart
     * the fade the band arrived with, and blink between every step.
     */
    const light = state => {
        const element = indicator();
        if (!element) {
            return;
        }

        element.classList.toggle('board-edge--lit', state !== null);
        element.classList.toggle('board-edge--cooling', state === 'cooling');
        element.classList.remove('board-edge--charging');
        if (state === 'armed') {
            void element.offsetWidth;
            element.classList.add('board-edge--charging');
        }
    };

    /**
     * The column strip at this height, if it has anywhere left to scroll. Chosen
     * by y alone: against the edge of the screen the pointer is outside every
     * strip's box horizontally, so hit-testing would find nothing.
     */
    const stripAt = y => [...document.querySelectorAll('.kanban-lane__zones')].find(strip => {
        const box = strip.getBoundingClientRect();
        // A little slack, so the gap between two bands isn't a dead spot.
        return y >= box.top - 8 && y <= box.bottom + 8 && strip.scrollWidth > strip.clientWidth + 1;
    });

    /**
     * Where each column comes to rest, in the strip's own scroll coordinates —
     * the same centring the CSS snap does, read back off the boxes rather than
     * recomputed from the widths.
     */
    const restingPoints = strip => {
        const box = strip.getBoundingClientRect();
        const furthest = strip.scrollWidth - strip.clientWidth;
        return [...strip.children].map(column => {
            const column_ = column.getBoundingClientRect();
            const left = column_.left - box.left + strip.scrollLeft;
            return Math.max(0, Math.min(left + column_.width / 2 - strip.clientWidth / 2, furthest));
        });
    };

    /** Steps one column; false when that edge has nothing left behind it. */
    const step = (strip, direction) => {
        const points = restingPoints(strip);
        const nearest = points.reduce(
            (best, point, i) =>
                Math.abs(point - strip.scrollLeft) < Math.abs(points[best] - strip.scrollLeft) ? i : best,
            0);
        const target = nearest + direction;
        if (target < 0 || target >= points.length) {
            return false;
        }

        strip.scrollTo({ left: points[target], behavior: 'smooth' });
        return true;
    };

    const charge = () => {
        light('armed');
        dwell = setTimeout(fire, DWELL_MS);
    };

    function fire() {
        dwell = null;
        const strip = stripAt(pointerY);
        if (!strip || !step(strip, side === 'right' ? 1 : -1)) {
            // Nothing left that way. Go dark but stay latched to this edge, so
            // the indicator doesn't blink once a second while the finger rests.
            light(null);
            return;
        }

        // Settle: the board has just moved under the pointer, and stepping again
        // straight away would page past the column that was being aimed for.
        light('cooling');
        cooldown = setTimeout(() => {
            cooldown = null;
            if (side) {
                charge();
            }
        }, COOLDOWN_MS);
    }

    const disarm = () => {
        clearTimeout(dwell);
        clearTimeout(cooldown);
        dwell = cooldown = null;
        if (side) {
            light(null);
            side = null;
        }
    };

    const arm = which => {
        if (which === side) {
            return;
        }

        disarm();
        // No steppable band at this height — say nothing rather than promise a
        // move that won't happen.
        if (!stripAt(pointerY)) {
            return;
        }

        side = which;
        charge();
    };

    window.solo.drag.on('move', drag => {
        if (drag.group !== 'task') {
            return;
        }

        pointerY = drag.y;
        if (drag.x <= EDGE_WIDTH_PX) {
            arm('left');
        } else if (drag.x >= window.innerWidth - EDGE_WIDTH_PX) {
            arm('right');
        } else {
            disarm();
        }
    });

    window.solo.drag.on('end', disarm);
})();
