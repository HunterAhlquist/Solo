'use strict';

// The About window's text arrives in the query string (main/about.js).
const params = new URLSearchParams(location.search);
const version = params.get('version');
document.getElementById('version').textContent = version ? `Version ${version}` : '';
document.getElementById('copyright').textContent = params.get('copyright') ?? '';

// The window is sized to the page, which grows when the game opens.
new ResizeObserver(() => {
    window.soloAbout?.fit(Math.ceil(document.body.getBoundingClientRect().height));
}).observe(document.body);

// ---------------------------------------------------------------------------
// Three clicks on the logo: a tiny falling-blocks game. No hold, no sound.
// ---------------------------------------------------------------------------

const COLS = 10;
const ROWS = 16;
const CELL = 8;
// Counted rather than read off event.detail, so three unhurried clicks count
// as well as a triple-click.
const CLICK_GAP_MS = 600;
const RETRY_DELAY_MS = 600;
// Holding ↓ drops a row this often, after a short wait so a tap is one row.
// The game keeps its own repeat because the OS's waits half a second and
// then runs at whatever rate the system is set to.
const SOFT_DROP_MS = 35;
const SOFT_DROP_DELAY_MS = 110;
// A landed piece locks after this many gravity ticks, however it got there —
// time to slide it or turn it. A hard drop locks at once, and so does a fresh
// ↓ press on a piece that has already landed.
const LOCK_TICKS = 1;
// How long a piece that has just locked flashes.
const FLASH_MS = 200;

// Colours come from the page (--piece-<type> in about.html): shades of Solo's
// own yellow or orange, by theme.
const PIECES = {
    I: { cells: [[0, 0, 0, 0], [1, 1, 1, 1], [0, 0, 0, 0], [0, 0, 0, 0]] },
    O: { cells: [[1, 1], [1, 1]] },
    T: { cells: [[0, 1, 0], [1, 1, 1], [0, 0, 0]] },
    S: { cells: [[0, 1, 1], [1, 1, 0], [0, 0, 0]] },
    Z: { cells: [[1, 1, 0], [0, 1, 1], [0, 0, 0]] },
    J: { cells: [[1, 0, 0], [1, 1, 1], [0, 0, 0]] },
    L: { cells: [[0, 0, 1], [1, 1, 1], [0, 0, 0]] },
};
// Tried in order after a rotation that doesn't fit where it is.
const KICKS = [[-1, 0], [1, 0], [0, -1], [-2, 0], [2, 0]];
const LINE_POINTS = [0, 100, 300, 500, 800];

const logo = document.getElementById('logo');
const game = document.getElementById('game');
const canvas = document.getElementById('board');
const scoreOut = document.getElementById('score');
const linesOut = document.getElementById('lines');
const overlay = document.getElementById('overlay');
const overlayTitle = document.getElementById('overlay-title');
const overlayHint = document.getElementById('overlay-hint');
const ctx = canvas.getContext('2d');
const nextCanvas = document.getElementById('next');
const nextCtx = nextCanvas.getContext('2d');

// A 1px gutter on every side of every cell, so the canvas is one pixel wider
// and taller than the cells alone.
const WIDTH = COLS * CELL + 1;
const HEIGHT = ROWS * CELL + 1;
canvas.style.width = `${WIDTH}px`;
canvas.style.height = `${HEIGHT}px`;
// Room for the widest piece (the I, four across) and the tallest lying flat.
const NEXT_WIDTH = 4 * CELL + 1;
const NEXT_HEIGHT = 2 * CELL + 1;
nextCanvas.style.width = `${NEXT_WIDTH}px`;
nextCanvas.style.height = `${NEXT_HEIGHT}px`;

let board;
let piece;
let bag = [];
let nextUp;
let score = 0;
let lines = 0;
// 'off' (the logo is showing), 'ready', 'playing', 'paused' or 'over'.
let state = 'off';
let dropTimer = 0;
let softDropping = false;
let softTimer = 0;
let lockTimer = 0;
// The cells of the piece that last locked, and when: { cells: [[x, y]], at }.
let flash = null;
let overAt = 0;
let lastFrame = 0;
let frameId = 0;

let clicks = 0;
let lastClick = 0;
logo.addEventListener('click', () => {
    const now = performance.now();
    clicks = now - lastClick < CLICK_GAP_MS ? clicks + 1 : 1;
    lastClick = now;
    if (clicks === 3) {
        clicks = 0;
        openGame();
    }
});

function openGame() {
    logo.hidden = true;
    game.hidden = false;
    sizeCanvas();
    reset();
    setState('ready');
}

function closeGame() {
    setState('off');
    game.hidden = true;
    logo.hidden = false;
}

function reset() {
    board = Array.from({ length: ROWS }, () => Array(COLS).fill(null));
    bag = [];
    nextUp = nextType();
    score = 0;
    lines = 0;
    dropTimer = 0;
    flash = null;
    showStats();
    spawn();
}

function setState(next) {
    state = next;
    cancelAnimationFrame(frameId);
    softDropping = false;
    if (state === 'over') {
        overAt = performance.now();
    }
    if (state === 'playing') {
        lastFrame = performance.now();
        frameId = requestAnimationFrame(frame);
    }

    const text = {
        ready: ['Ready', 'press any key'],
        paused: ['Paused', 'press any key'],
        over: ['Game over', 'space to retry'],
    }[state];
    overlay.hidden = !text;
    if (text) {
        [overlayTitle.textContent, overlayHint.textContent] = text;
    }
    draw();
}

// ---- rules ----------------------------------------------------------------

function level() {
    return Math.floor(lines / 10);
}

function dropInterval() {
    return Math.max(70, 800 * Math.pow(0.85, level()));
}

// A 7-bag: every piece once per seven, in a shuffled order.
function nextType() {
    if (bag.length === 0) {
        bag = Object.keys(PIECES);
        for (let i = bag.length - 1; i > 0; i--) {
            const j = Math.floor(Math.random() * (i + 1));
            [bag[i], bag[j]] = [bag[j], bag[i]];
        }
    }
    return bag.pop();
}

function spawn() {
    // A held ↓ stops with the piece it was dropping; the next needs a new press.
    softDropping = false;
    lockTimer = 0;
    const type = nextUp;
    nextUp = nextType();
    const cells = PIECES[type].cells;
    // The I's cells sit on the second row of its box; up one puts them on top.
    piece = { type, cells, x: Math.floor((COLS - cells.length) / 2), y: type === 'I' ? -1 : 0 };
    if (collides(cells, piece.x, piece.y)) {
        setState('over');
    }
}

// Rows above the board (y < 0) are open air, so a piece can rotate at the top.
function collides(cells, px, py) {
    for (let y = 0; y < cells.length; y++) {
        for (let x = 0; x < cells.length; x++) {
            if (!cells[y][x]) continue;
            const bx = px + x;
            const by = py + y;
            if (bx < 0 || bx >= COLS || by >= ROWS || (by >= 0 && board[by][bx])) {
                return true;
            }
        }
    }
    return false;
}

function rotated(cells, clockwise) {
    const n = cells.length;
    return cells.map((row, y) => row.map((_, x) =>
        clockwise ? cells[n - 1 - x][y] : cells[x][n - 1 - y]));
}

function tryMove(dx, dy) {
    if (collides(piece.cells, piece.x + dx, piece.y + dy)) {
        return false;
    }
    piece.x += dx;
    piece.y += dy;
    return true;
}

function rotate(clockwise) {
    const cells = rotated(piece.cells, clockwise);
    for (const [dx, dy] of [[0, 0], ...KICKS]) {
        if (!collides(cells, piece.x + dx, piece.y + dy)) {
            piece.cells = cells;
            piece.x += dx;
            piece.y += dy;
            return;
        }
    }
}

function lock() {
    const placed = [];
    for (let y = 0; y < piece.cells.length; y++) {
        for (let x = 0; x < piece.cells.length; x++) {
            if (!piece.cells[y][x]) continue;
            if (piece.y + y < 0) {
                // Landed partly above the board: topped out.
                setState('over');
                return;
            }
            board[piece.y + y][piece.x + x] = piece.type;
            placed.push([piece.x + x, piece.y + y]);
        }
    }

    const full = board.map((row, y) => row.every(Boolean) ? y : -1).filter(y => y >= 0);
    // Where the piece's cells are once the full rows are gone: a cell in a
    // cleared row is gone with it, and the rest move down one per cleared row
    // beneath them.
    flash = {
        cells: placed
            .filter(([, y]) => !full.includes(y))
            .map(([x, y]) => [x, y + full.filter(row => row > y).length]),
        at: performance.now(),
    };

    const kept = board.filter((_, y) => !full.includes(y));
    const cleared = full.length;
    if (cleared > 0) {
        score += LINE_POINTS[cleared] * (level() + 1);
        lines += cleared;
        board = [...Array.from({ length: cleared }, () => Array(COLS).fill(null)), ...kept];
        showStats();
    }

    dropTimer = 0;
    spawn();
}

// False once the piece is resting on something; the lock delay takes it from
// there, held ↓ or not.
function softDrop() {
    if (tryMove(0, 1)) {
        score += 1;
        showStats();
        dropTimer = 0;
        return true;
    }
    return false;
}

function grounded() {
    return collides(piece.cells, piece.x, piece.y + 1);
}

function hardDrop() {
    let fell = 0;
    while (tryMove(0, 1)) {
        fell++;
    }
    score += fell * 2;
    showStats();
    lock();
}

function showStats() {
    scoreOut.textContent = String(score);
    linesOut.textContent = String(lines);
}

// ---- loop and input -------------------------------------------------------

function frame(now) {
    if (state !== 'playing') return;
    // Capped, so a stalled frame never drops a piece several rows at once.
    const elapsed = Math.min(now - lastFrame, 250);
    lastFrame = now;

    if (softDropping) {
        softTimer += elapsed;
        while (softDropping && softTimer >= SOFT_DROP_MS) {
            softTimer -= SOFT_DROP_MS;
            softDrop();
        }
    }

    if (state === 'playing') {
        dropTimer += elapsed;
        if (dropTimer >= dropInterval()) {
            dropTimer = 0;
            tryMove(0, 1);
        }

        // Counts only while resting: a piece slid off a ledge falls again
        // and gets the full delay where it next lands.
        if (grounded()) {
            lockTimer += elapsed;
            if (lockTimer >= LOCK_TICKS * dropInterval()) {
                lock();
            }
        } else {
            lockTimer = 0;
        }
    }
    draw();
    if (state === 'playing') {
        frameId = requestAnimationFrame(frame);
    }
}

window.addEventListener('keydown', event => {
    // Leaves Cmd+W and the rest of the menu shortcuts alone.
    if (state === 'off' || event.metaKey || event.ctrlKey || event.altKey) {
        return;
    }

    if (event.key === 'Escape') {
        closeGame();
        event.preventDefault();
        return;
    }

    if (state === 'ready' || state === 'paused') {
        setState('playing');
        event.preventDefault();
        return;
    }

    if (state === 'over') {
        // Whoever just topped out was probably mid-press — Space rotates —
        // so it has to be a fresh press, a moment later, to retry.
        if ((event.key === ' ' || event.key === 'Enter') &&
            !event.repeat && performance.now() - overAt > RETRY_DELAY_MS) {
            reset();
            setState('playing');
        }
        event.preventDefault();
        return;
    }

    switch (event.key) {
        case 'ArrowLeft': tryMove(-1, 0); break;
        case 'ArrowRight': tryMove(1, 0); break;
        case 'ArrowDown':
            // The OS's repeats are ignored: the frame loop drops while held.
            // Pressing again once landed means "here", so it skips the wait.
            if (event.repeat) {
                break;
            }
            if (grounded()) {
                lock();
            } else if (softDrop()) {
                softDropping = true;
                softTimer = -SOFT_DROP_DELAY_MS;
            }
            break;
        case 'ArrowUp': hardDrop(); break;
        case ' ':
        case 'x':
        case 'X':
            rotate(true);
            break;
        case 'z':
        case 'Z':
            rotate(false);
            break;
        case 'p':
        case 'P':
            setState('paused');
            break;
        default: return;
    }
    event.preventDefault();
    draw();
});

window.addEventListener('keyup', event => {
    if (event.key === 'ArrowDown') {
        softDropping = false;
    }
});

// Nobody is playing a window they've clicked away from.
window.addEventListener('blur', () => {
    if (state === 'playing') {
        setState('paused');
    }
});

// ---- drawing --------------------------------------------------------------

function sizeCanvas() {
    const scale = window.devicePixelRatio || 1;
    canvas.width = WIDTH * scale;
    canvas.height = HEIGHT * scale;
    ctx.setTransform(scale, 0, 0, scale, 0, 0);
    nextCanvas.width = NEXT_WIDTH * scale;
    nextCanvas.height = NEXT_HEIGHT * scale;
    nextCtx.setTransform(scale, 0, 0, scale, 0, 0);
}

// Dragging the window to a display of another density. The query names the
// density it was made for, so it is made again after every change.
function watchDensity() {
    matchMedia(`(resolution: ${window.devicePixelRatio}dppx)`).addEventListener('change', () => {
        if (state !== 'off') {
            sizeCanvas();
            draw();
        }
        watchDensity();
    }, { once: true });
}
watchDensity();
matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => draw());

function drawCell(x, y, color, target = ctx) {
    const left = x * CELL + 1;
    const top = y * CELL + 1;
    target.fillStyle = color;
    target.fillRect(left, top, CELL - 1, CELL - 1);
    target.fillStyle = 'rgb(255 255 255 / 30%)';
    target.fillRect(left, top, CELL - 1, 1);
}

// The next piece, trimmed to the cells it fills and centred in its box.
function drawNext(palette) {
    nextCtx.clearRect(0, 0, NEXT_WIDTH, NEXT_HEIGHT);
    const { cells } = PIECES[nextUp];
    const color = palette.pieces[nextUp];
    const rows = cells.map((row, y) => row.some(Boolean) ? y : -1).filter(y => y >= 0);
    const cols = cells[0].map((_, x) => cells.some(row => row[x]) ? x : -1).filter(x => x >= 0);
    const dx = (NEXT_WIDTH - 1 - cols.length * CELL) / 2 - cols[0] * CELL;
    const dy = (NEXT_HEIGHT - 1 - rows.length * CELL) / 2 - rows[0] * CELL;
    nextCtx.save();
    nextCtx.translate(dx, dy);
    eachCell(cells, (x, y) => drawCell(x, y, color, nextCtx));
    nextCtx.restore();
}

function eachCell(cells, fn) {
    for (let y = 0; y < cells.length; y++) {
        for (let x = 0; x < cells.length; x++) {
            if (cells[y][x]) fn(x, y);
        }
    }
}

// Read on every draw, so a theme change shows on the next frame.
function readPalette() {
    const style = getComputedStyle(document.documentElement);
    const pieces = {};
    for (const type of Object.keys(PIECES)) {
        pieces[type] = style.getPropertyValue(`--piece-${type}`).trim();
    }
    return { pieces, empty: style.getPropertyValue('--cell').trim() };
}

function draw() {
    if (state === 'off' || !board) return;

    const palette = readPalette();
    const { empty } = palette;
    drawNext(palette);
    ctx.clearRect(0, 0, WIDTH, HEIGHT);
    for (let y = 0; y < ROWS; y++) {
        for (let x = 0; x < COLS; x++) {
            const type = board[y][x];
            if (type) {
                drawCell(x, y, palette.pieces[type]);
            } else {
                ctx.fillStyle = empty;
                ctx.fillRect(x * CELL + 1, y * CELL + 1, CELL - 1, CELL - 1);
            }
        }
    }

    if (flash) {
        const t = (performance.now() - flash.at) / FLASH_MS;
        if (t < 1) {
            ctx.fillStyle = `rgb(255 255 255 / ${Math.round((1 - t) * 85)}%)`;
            for (const [x, y] of flash.cells) {
                ctx.fillRect(x * CELL + 1, y * CELL + 1, CELL - 1, CELL - 1);
            }
        } else {
            flash = null;
        }
    }

    if (state === 'over') return;

    const color = palette.pieces[piece.type];
    let ghostY = piece.y;
    while (!collides(piece.cells, piece.x, ghostY + 1)) {
        ghostY++;
    }

    ctx.globalAlpha = 0.22;
    eachCell(piece.cells, (x, y) => {
        if (ghostY + y >= 0) drawCell(piece.x + x, ghostY + y, color);
    });
    ctx.globalAlpha = 1;
    eachCell(piece.cells, (x, y) => {
        if (piece.y + y >= 0) drawCell(piece.x + x, piece.y + y, color);
    });
}
