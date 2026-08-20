// ---------------------------------------------------------------------------
// Dragging. Everything the board's drag & drop needs that the browser won't do
// on its own — kept together here rather than scattered through solo-interop.js,
// because the three pieces below only make sense as one story:
//
//   1. a WebKit workaround for native drags that carry no payload,
//   2. a touch drag of our own, because a finger's drag is not the browser's,
//   3. the screen-edge gestures both of them share.
//
// Nothing here knows anything about Blazor. The drag is expressed as the very
// same DragEvents the browser fires, so MudDropContainer, the epic bands and the
// sub-task checklist all handle a finger exactly as they handle a mouse.
// ---------------------------------------------------------------------------

// WebKit cancels a drag whose dragstart wrote nothing to the pasteboard, so on
// iPadOS the press falls through to the text-selection gesture and nothing ever
// drags. Blazor's DragEventArgs is a read-only snapshot — the hand-rolled drag
// sources (epic bands, sub-task rows) can't call setData from C# — so seed the
// payload here, in capture, before their @ondragstart runs. Only for a drag
// that carries nothing: a text, link or image drag already has its own data,
// and MudBlazor's cards set theirs (which is why those alone dragged on iPad).
// The type is deliberately '' rather than 'text/plain', as MudBlazor's is: a
// stray drop into a text field must not paste anything.
document.addEventListener('dragstart', e => {
    if (e.dataTransfer && e.dataTransfer.types.length === 0) {
        e.dataTransfer.setData('', 'solo-drag');
    }
}, true);

// How near an edge of the screen counts as being held against it, and how long
// to hold before the board steps a column. Shared by both drag paths.
const EDGE_WIDTH = 44;
const EDGE_DWELL_MS = 1000;

// ---------------------------------------------------------------------------
// Dragging a card to a column that is off-screen (the phone carousel).
//
// Below the md breakpoint each band's three status columns become a horizontal
// scroller, so the column you mean to drop into is usually not on screen when
// you pick a card up. Holding the card against either edge of the screen steps
// that band one column that way, the way holding a dragged file over a folder
// opens it; keep holding and it keeps stepping. Then drag back into the middle
// and let go — the edges are signage, not drop zones, so the drop still lands
// wherever the finger is.
//
// Driven from DragEvents, so it serves a mouse and a finger alike. The two
// indicator elements belong to Board.razor; this only toggles a class on them.
// ---------------------------------------------------------------------------
(() => {
    let dragging = false;
    let side = null;
    let timer = null;
    let pointerY = 0;

    const indicator = () => document.querySelector(`.board-edge--${side}`);

    // The band at this height, if it has anywhere left to scroll. Chosen by y
    // alone: at the edge of the screen the pointer is outside every band's box
    // horizontally, so hit-testing would find nothing. This is also what keeps
    // the whole feature off the desktop grid, where no band ever scrolls.
    const bandAt = y => [...document.querySelectorAll('.kanban-lane__zones')].find(zones => {
        const box = zones.getBoundingClientRect();
        // A little slack, so the gap between two bands isn't a dead spot.
        return y >= box.top - 8 && y <= box.bottom + 8 &&
            zones.scrollWidth > zones.clientWidth + 1;
    });

    // Where each column comes to rest, in the band's own scroll coordinates.
    // The same centring the CSS does, read back off the boxes rather than
    // recomputed from the widths.
    const restingPoints = zones => {
        const strip = zones.getBoundingClientRect();
        const furthest = zones.scrollWidth - zones.clientWidth;
        return [...zones.children].map(column => {
            const box = column.getBoundingClientRect();
            const left = box.left - strip.left + zones.scrollLeft;
            return Math.max(0, Math.min(left + box.width / 2 - zones.clientWidth / 2, furthest));
        });
    };

    // Steps one column; false when that edge has nothing left behind it.
    const step = (zones, direction) => {
        const points = restingPoints(zones);
        const nearest = points.reduce(
            (best, point, i) =>
                Math.abs(point - zones.scrollLeft) < Math.abs(points[best] - zones.scrollLeft) ? i : best,
            0);
        const target = nearest + direction;
        if (target < 0 || target >= points.length) {
            return false;
        }

        zones.scrollTo({ left: points[target], behavior: 'smooth' });
        return true;
    };

    // Lights the edge and starts the dwell. Removing the class and forcing a
    // reflow is what restarts the charge animation for a second step.
    const charge = () => {
        const element = indicator();
        if (element) {
            element.classList.remove('board-edge--armed');
            void element.offsetWidth;
            element.classList.add('board-edge--armed');
        }

        timer = setTimeout(fire, EDGE_DWELL_MS);
    };

    function fire() {
        timer = null;
        const band = bandAt(pointerY);
        if (band && step(band, side === 'right' ? 1 : -1)) {
            charge();
            return;
        }

        // Nothing left that way. Go dark but stay latched to this edge, so the
        // indicator doesn't blink once a second while the finger rests there.
        indicator()?.classList.remove('board-edge--armed');
    }

    const disarm = () => {
        clearTimeout(timer);
        timer = null;
        if (side) {
            indicator()?.classList.remove('board-edge--armed');
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
        if (!bandAt(pointerY)) {
            return;
        }

        side = which;
        charge();
    };

    // Only a card: the epic bands are their own drag, and reordering them is a
    // vertical business with no columns to step through.
    document.addEventListener('dragstart', e => {
        dragging = e.target instanceof Element && e.target.closest('.mud-drop-item') !== null;
    }, true);

    // Capture, and strictly a reader: preventDefault here would tell the browser
    // this is a drop target and swallow the real one.
    document.addEventListener('dragover', e => {
        if (!dragging) {
            return;
        }

        pointerY = e.clientY;
        if (e.clientX <= EDGE_WIDTH) {
            arm('left');
        } else if (e.clientX >= window.innerWidth - EDGE_WIDTH) {
            arm('right');
        } else {
            disarm();
        }
    }, true);

    for (const ending of ['dragend', 'drop']) {
        document.addEventListener(ending, () => {
            dragging = false;
            disarm();
        }, true);
    }
})();

// ---------------------------------------------------------------------------
// Touch dragging.
//
// A finger's drag is not the browser's. Chrome and Firefox on Android fire no
// DragEvent at all from a touch, and where WebKit does, the gesture is the
// platform's: it lifts on its own schedule, it fights the text-selection
// callout for the same press, its preview is clipped by whatever scroller the
// card sits in — which on the phone board is a horizontal one — and it will not
// scroll the page to reach a band further down.
//
// So on a touchscreen the drag is ours. A press has to be held before anything
// lifts, which is what keeps an ordinary swipe a swipe; the card that lifts is
// a copy parented to <body>, so no scroller can crop it; the drop lands on
// whatever is under the finger when it goes up; and dragging towards the top or
// bottom of the screen scrolls whatever is scrollable underneath.
//
// What it does *not* do is invent a protocol. It dispatches the same DragEvent
// sequence the browser would — dragstart, dragenter/dragleave, dragover, drop,
// dragend — at the element under the finger, so everything downstream (the
// board's MudDropContainer, the epic bands, the sub-task checklist, and the
// edge gestures above) is handling an ordinary drag and cannot tell the
// difference. Native dragging stays in charge of a mouse: this runs only where
// the primary pointer is coarse, which is also where the CSS gives up
// -webkit-user-drag, so the two never both fire.
// ---------------------------------------------------------------------------
(() => {
    // Hold this long before anything lifts, and let the finger wander this far
    // in the meantime before deciding it was a scroll after all.
    const HOLD_MS = 1000;
    const SLOP_PX = 10;

    // Dragging into this band at the top or bottom scrolls, faster the further
    // in you go, up to a frame's worth.
    const SCROLL_BAND_PX = 72;
    const SCROLL_MAX_PX = 16;

    const coarse = () => window.matchMedia('(pointer: coarse)').matches;

    let source = null;      // what was picked up
    let ghost = null;       // the copy that follows the finger
    let transfer = null;    // the DataTransfer carried through the whole drag
    let over = null;        // the element the last dragenter went to
    let hold = null;        // the press timer, before the lift
    let frame = null;       // the auto-scroll loop, after it
    let startX = 0, startY = 0, x = 0, y = 0;

    const dragging = () => source !== null && ghost !== null;

    const fire = (element, type) => element?.dispatchEvent(new DragEvent(type, {
        bubbles: true, cancelable: true, dataTransfer: transfer, clientX: x, clientY: y,
    }));

    // The ghost never takes a hit: pointer-events:none keeps it out of
    // elementFromPoint, so what is under the finger is what was always there.
    const under = () => document.elementFromPoint(
        Math.max(0, Math.min(x, window.innerWidth - 1)),
        Math.max(0, Math.min(y, window.innerHeight - 1)));

    // The nearest thing that can scroll vertically, so a checklist inside the
    // drawer scrolls itself rather than the page behind it.
    const scroller = element => {
        for (let node = element; node && node !== document.body; node = node.parentElement) {
            const style = getComputedStyle(node);
            if (/(auto|scroll)/.test(style.overflowY) && node.scrollHeight > node.clientHeight + 1) {
                return node;
            }
        }

        return null;
    };

    const autoScroll = () => {
        frame = requestAnimationFrame(autoScroll);

        const from = y < SCROLL_BAND_PX ? y - SCROLL_BAND_PX
            : y > window.innerHeight - SCROLL_BAND_PX ? y - (window.innerHeight - SCROLL_BAND_PX)
            : 0;
        if (from === 0) {
            return;
        }

        const by = Math.round(SCROLL_MAX_PX * Math.max(-1, Math.min(from / SCROLL_BAND_PX, 1)));
        const target = scroller(over ?? document.body);
        if (target) {
            target.scrollTop += by;
        } else {
            window.scrollBy(0, by);
        }
    };

    const lift = () => {
        hold = null;
        transfer = new DataTransfer();
        fire(source, 'dragstart');

        const box = source.getBoundingClientRect();
        ghost = source.cloneNode(true);
        ghost.classList.add('solo-drag-ghost');
        ghost.style.width = `${box.width}px`;
        ghost.style.height = `${box.height}px`;
        ghost.style.left = `${box.left}px`;
        ghost.style.top = `${box.top}px`;
        document.body.appendChild(ghost);
        source.classList.add('solo-drag-source');

        // A short tick, so the lift is felt as well as seen.
        navigator.vibrate?.(10);
        follow();
        frame = requestAnimationFrame(autoScroll);
    };

    const follow = () => {
        ghost.style.transform = `translate(${x - startX}px, ${y - startY}px)`;

        const now = under();
        if (now !== over) {
            fire(over, 'dragleave');
            over = now;
            fire(over, 'dragenter');
        }

        fire(over, 'dragover');
    };

    const finish = drop => {
        cancelAnimationFrame(frame);
        frame = null;
        if (drop) {
            fire(over, 'drop');
        } else {
            fire(over, 'dragleave');
        }

        fire(source, 'dragend');
        ghost.remove();
        source.classList.remove('solo-drag-source');
        source = ghost = transfer = over = null;
    };

    document.addEventListener('touchstart', e => {
        if (!coarse() || e.touches.length !== 1 || dragging()) {
            return;
        }

        const target = e.target instanceof Element ? e.target : null;
        // A control inside a drag source is a control first: the epic band's own
        // add and delete buttons live inside the band that drags.
        if (!target || target.closest('button, a, input, textarea, [role="button"]')) {
            return;
        }

        const handle = target.closest('[draggable="true"]');
        if (!handle) {
            return;
        }

        source = handle;
        startX = x = e.touches[0].clientX;
        startY = y = e.touches[0].clientY;
        hold = setTimeout(lift, HOLD_MS);
        watch();
    }, { passive: true });

    // Attached only for the life of a press that landed on something draggable,
    // and non-passive only then: a permanent non-passive touchmove on the
    // document takes every scroll on every page off the browser's fast path.
    const watch = () => document.addEventListener('touchmove', track, { passive: false });
    const unwatch = () => document.removeEventListener('touchmove', track, { passive: false });

    function track(e) {
        x = e.touches[0].clientX;
        y = e.touches[0].clientY;

        if (!dragging()) {
            // Still deciding. Wander too far and this was a scroll all along —
            // and it stays a scroll, because nothing here has taken the event.
            if (Math.abs(x - startX) > SLOP_PX || Math.abs(y - startY) > SLOP_PX) {
                clearTimeout(hold);
                hold = null;
                source = null;
                unwatch();
            }

            return;
        }

        // Taking the gesture. Nothing has scrolled yet — the press was held
        // still — so the browser still honours this.
        e.preventDefault();
        follow();
    }

    for (const ending of ['touchend', 'touchcancel']) {
        document.addEventListener(ending, e => {
            if (!source) {
                return;
            }

            if (!dragging()) {
                clearTimeout(hold);
                hold = null;
                source = null;
                unwatch();
                return;
            }

            // Where the finger actually left, which is not always where it was
            // last seen moving — a drop belongs to the spot it was let go over.
            const last = e.changedTouches[0];
            if (last) {
                x = last.clientX;
                y = last.clientY;
                follow();
            }

            finish(ending === 'touchend');
            unwatch();
        }, { passive: true });
    }
})();
