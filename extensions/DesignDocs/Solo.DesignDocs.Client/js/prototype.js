import * as Y from 'yjs';
import { Awareness, encodeAwarenessUpdate, applyAwarenessUpdate } from 'y-protocols/awareness';
import { EditorState, Compartment } from '@codemirror/state';
import { EditorView, keymap } from '@codemirror/view';
import { defaultKeymap } from '@codemirror/commands';
import { markdown } from '@codemirror/lang-markdown';
import { yCollab, yUndoManagerKeymap } from 'y-codemirror.next';
import { encode, decode, snapshot, apply, pressureWidth, stampOffset } from './prototype-protocol.js';

// B0 only: peers exchange full state on rejoin. There is deliberately no durable
// relay here; that protocol is B3 and cannot be inferred from this experiment.
export function mountPrototype(editorRoot, sketchRoot, metrics, bridge, options) {
    const doc = new Y.Doc(), text = doc.getText('markdown'), strokes = doc.getArray('strokes');
    const awareness = new Awareness(doc), remote = {}, readOnly = new Compartment();
    const pending = new Map(), rtts = [], deliveries = [];
    let online = false, disposed = false, batch = [], batchTimer, awarenessTimer, probeTimer;
    let sends = 0, received = 0, failed = 0, pointerEvents = 0, samples = 0, minimum = 1, maximum = 0;
    let paintLatency = 0, active = null, penDown = false, pointerType = 'none';
    const user = { name: options.name, userId: options.userId, color: '#9c6320', colorLight: '#9c632033' };
    awareness.setLocalState(options.canWrite ? { user } : null);

    const editor = new EditorView({ parent: editorRoot, state: EditorState.create({ doc: text.toString(), extensions: [
        markdown(), keymap.of([...defaultKeymap, ...yUndoManagerKeymap]),
        EditorView.lineWrapping, yCollab(text, awareness),
        readOnly.of([EditorState.readOnly.of(true), EditorView.editable.of(false)]),
    ] }) });

    async function send(kind, payload) {
        if (disposed || !online) return false;
        const data = JSON.stringify(payload);
        if (data.length > 48000) { failed++; return false; }
        try {
            sends++;
            const result = await bridge.invokeMethodAsync('Send', kind, data);
            if (result !== 0) failed++;
            return result === 0;
        } catch { failed++; return false; }
    }
    function packet(bytes) { return { id: crypto.randomUUID(), sentAt: Date.now(), data: encode(bytes) }; }
    function flush() {
        clearTimeout(batchTimer);
        if (!batch.length) return;
        const p = packet(Y.mergeUpdates(batch));
        batch = [];
        pending.set(p.id, p);
        void send('update', p);
    }
    doc.on('update', (bytes, origin) => {
        if (origin === remote) return;
        batch.push(bytes);
        batchTimer ??= setTimeout(() => { batchTimer = null; flush(); }, 50);
    });
    awareness.on('update', ({ added, updated, removed }, origin) => {
        if (origin === remote || !options.canWrite) return;
        clearTimeout(awarenessTimer);
        awarenessTimer = setTimeout(() => {
            void send('awareness', { data: encode(encodeAwarenessUpdate(awareness, [...added, ...updated, ...removed])) });
        }, 60);
    });

    const toolbar = document.createElement('div');
    toolbar.className = 'design-docs-probe-tools';
    const levelLabel = document.createElement('label');
    levelLabel.textContent = 'Minimum width ';
    const level = document.createElement('select');
    level.setAttribute('aria-label', 'Minimum width');
    for (const [i, width] of [1, 2, 3, 5, 8].entries()) {
        const choice = document.createElement('option');
        choice.value = i; choice.textContent = `${i + 1} (${width} px)`; level.append(choice);
    }
    levelLabel.append(level);
    const coalescedLabel = document.createElement('label'), coalesced = document.createElement('input');
    coalesced.type = 'checkbox'; coalesced.checked = true;
    coalescedLabel.append(coalesced, ' Use coalesced events');
    toolbar.append(levelLabel, coalescedLabel);
    const canvas = document.createElement('canvas');
    canvas.width = 595; canvas.height = 842; canvas.setAttribute('aria-label', 'Pencil test page');
    canvas.style.cssText = 'width:min(100%,595px);height:auto;touch-action:none;image-rendering:pixelated;user-select:none;background:white;border:1px solid #777';
    sketchRoot.append(toolbar, canvas);
    const ctx = canvas.getContext('2d');
    ctx.imageSmoothingEnabled = false;
    function drawStroke(stroke, frame) {
        ctx.fillStyle = '#111';
        let stamp = 0;
        for (let i = 0; i < stroke.points.length; i++) {
            const a = stroke.points[Math.max(0, i - 1)], b = stroke.points[i];
            const distance = Math.max(1, Math.ceil(Math.hypot(b[0] - a[0], b[1] - a[1])));
            for (let step = 0; step <= distance; step++) {
                const t = step / distance, width = pressureWidth(stroke.level, b[2], stroke.pointerType);
                const [dx, dy] = stampOffset(stroke.seed, stamp++, frame);
                ctx.fillRect(Math.round(a[0] + (b[0] - a[0]) * t + dx - width / 2),
                    Math.round(a[1] + (b[1] - a[1]) * t + dy - width / 2), width, width);
            }
        }
    }
    const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');
    let visible = true, frame = 0;
    const observer = new IntersectionObserver(entries => { visible = entries[0].isIntersecting; });
    observer.observe(canvas);
    function draw() {
        ctx.clearRect(0, 0, canvas.width, canvas.height);
        for (const stroke of strokes.toArray()) drawStroke(stroke, frame);
        if (active) drawStroke(active, 0);
    }
    strokes.observe(draw);
    const boil = setInterval(() => {
        if (!visible || document.hidden || disposed) return;
        frame = reducedMotion.matches ? 0 : (frame + 1) % 3;
        draw();
    }, 250);
    function point(event) {
        const rect = canvas.getBoundingClientRect();
        minimum = Math.min(minimum, event.pressure); maximum = Math.max(maximum, event.pressure);
        samples++;
        return [(event.clientX - rect.left) * canvas.width / rect.width,
            (event.clientY - rect.top) * canvas.height / rect.height, event.pressure];
    }
    function down(event) {
        if (!online || !options.canWrite || active || event.pointerType === 'touch' && penDown) return;
        event.preventDefault();
        pointerType = event.pointerType; penDown = pointerType === 'pen'; pointerEvents++;
        active = { id: crypto.randomUUID(), pointerId: event.pointerId, pointerType, level: Number(level.value),
            seed: Math.floor(Math.random() * 2147483647), points: [point(event)] };
        canvas.setPointerCapture(event.pointerId);
        draw();
    }
    function move(event) {
        if (!active || event.pointerId !== active.pointerId) return;
        event.preventDefault(); pointerEvents++;
        const started = performance.now();
        const gathered = coalesced.checked && event.getCoalescedEvents ? event.getCoalescedEvents() : [];
        for (const e of gathered.length ? gathered : [event]) active.points.push(point(e));
        draw();
        paintLatency = Math.max(paintLatency, performance.now() - started);
    }
    function up(event) {
        if (!active || active.pointerId !== event.pointerId) return;
        active.points.push(point(event));
        if (online && options.canWrite && event.type !== 'pointercancel') strokes.push([active]);
        active = null; penDown = false; draw();
    }
    const blockMenu = event => event.preventDefault();
    canvas.addEventListener('pointerdown', down);
    canvas.addEventListener('pointermove', move);
    canvas.addEventListener('pointerup', up);
    canvas.addEventListener('pointercancel', up);
    canvas.addEventListener('contextmenu', blockMenu);
    function report() {
        const percentile = values => values.length ? [...values].sort((a, b) => a - b)[Math.floor((values.length - 1) * .95)] : 0;
        metrics.textContent = `${online ? 'Connected' : 'Offline — read-only'} · ${text.length} characters · ` +
            `${sends} messages sent · ${received} received · ${pending.size} unacked · ${failed} failures\n` +
            `Acknowledgment p95 ${percentile(rtts)} ms · delivery p95 ${percentile(deliveries)} ms · ` +
            `${strokes.length} strokes · ${pointerEvents} pointer events / ${samples} samples (${pointerType})\n` +
            `Pressure ${samples ? minimum.toFixed(3) : '—'}–${samples ? maximum.toFixed(3) : '—'} · max drawing handler ${paintLatency.toFixed(2)} ms`;
    }
    const reporting = setInterval(report, 500);
    const unload = event => { if (pending.size || batch.length) { event.preventDefault(); event.returnValue = ''; } };
    window.addEventListener('beforeunload', unload);
    report();
    return {
        receive(kind, raw) {
            if (disposed || !online || !raw) return;
            received++;
            if (kind === 'refused') { failed++; report(); return; }
            const payload = JSON.parse(raw);
            if (kind === 'sync' && options.canWrite) void send('state', { data: snapshot(doc), sentAt: Date.now() });
            else if (kind === 'update' || kind === 'state') {
                apply(doc, payload.data, remote);
                deliveries.push(Math.max(0, Date.now() - payload.sentAt));
            } else if (kind === 'ack') {
                pending.delete(payload.id); rtts.push(Date.now() - payload.sentAt);
            } else if (kind === 'awareness') applyAwarenessUpdate(awareness, decode(payload.data), remote);
            report();
        },
        connected(value) {
            online = value;
            editor.dispatch({ effects: readOnly.reconfigure([
                EditorState.readOnly.of(!online || !options.canWrite), EditorView.editable.of(online && options.canWrite),
            ]) });
            if (online) {
                void send('sync', {});
                for (const p of pending.values()) void send('update', p);
                if (options.canWrite) void send('state', { data: snapshot(doc), sentAt: Date.now() });
            }
            report();
        },
        probe() {
            if (!online || !options.canWrite || probeTimer) return;
            let count = 0;
            probeTimer = setInterval(() => {
                if (!online || ++count > 200) { clearInterval(probeTimer); probeTimer = null; return; }
                text.insert(text.length, ` [${count}]`);
            }, 10);
        },
        dispose() {
            disposed = true;
            clearTimeout(batchTimer); clearTimeout(awarenessTimer); clearInterval(probeTimer);
            clearInterval(reporting); clearInterval(boil); observer.disconnect();
            window.removeEventListener('beforeunload', unload);
            canvas.removeEventListener('pointerdown', down); canvas.removeEventListener('pointermove', move);
            canvas.removeEventListener('pointerup', up); canvas.removeEventListener('pointercancel', up);
            canvas.removeEventListener('contextmenu', blockMenu);
            editor.destroy(); awareness.destroy(); doc.destroy(); sketchRoot.replaceChildren(); metrics.textContent = '';
        },
    };
}
