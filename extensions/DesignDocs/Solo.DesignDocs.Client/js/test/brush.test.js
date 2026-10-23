import { test } from 'node:test';
import assert from 'node:assert/strict';
import { BOIL_FRAMES, LEVEL_WIDTHS, boilOffset, brushMask, stamps, widthFor } from '../src/sketch/brush.js';

test('a stylus ramps from the chosen level toward the thickest; mouse and touch ignore pressure', () => {
    assert.equal(widthFor(2, 0, 'pen'), LEVEL_WIDTHS[1]);
    assert.equal(widthFor(2, 1, 'pen'), LEVEL_WIDTHS[LEVEL_WIDTHS.length - 1]);
    assert.ok(widthFor(2, 0.5, 'pen') > LEVEL_WIDTHS[1]);
    assert.equal(widthFor(2, 0.5, 'mouse'), LEVEL_WIDTHS[1]);
    assert.equal(widthFor(2, 0.9, 'touch'), LEVEL_WIDTHS[1]);
    assert.equal(widthFor(99, 0, 'mouse'), LEVEL_WIDTHS[LEVEL_WIDTHS.length - 1]);
});

test('stamps land on whole pixels and repeat exactly for the same stroke', () => {
    const points = [10.2, 10.7, 0.3, 40.5, 22.1, 0.6, 80, 5, 0.9];
    const first = stamps(points, 3, 'pen');
    const second = stamps([...points], 3, 'pen');
    assert.deepEqual(first, second);
    assert.ok(first.length > 10);
    assert.ok(first.every(s => Number.isInteger(s.x) && Number.isInteger(s.y) && s.angle >= 0 && s.angle < 4));
});

test('boil offsets are still on frame 0 and at most one pixel otherwise', () => {
    let moved = 0;
    for (let index = 0; index < 300; index++) {
        assert.deepEqual(boilOffset(1234, index, 0), [0, 0]);
        for (let frame = 1; frame < BOIL_FRAMES; frame++) {
            const [x, y] = boilOffset(1234, index, frame);
            assert.ok(Math.abs(x) <= 1 && Math.abs(y) <= 1);
            assert.deepEqual(boilOffset(1234, index, frame), [x, y]);
            moved += x !== 0 || y !== 0 ? 1 : 0;
        }
    }

    assert.ok(moved > 0 && moved < 300, `${moved} of 600 stamps moved`);
});

test('brush masks grow with width', () => {
    assert.equal(brushMask(1, 0).length, 1);
    assert.equal(brushMask(2, 0).length, 4);
    assert.ok(brushMask(8, 1).length > brushMask(5, 1).length);
});
