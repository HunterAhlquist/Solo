// Pages at 72 paint pixels per inch, the starting density Hunter chose to tune
// from: A4 is 595 × 842 paint pixels, shown scaled up with pixels kept square.

export const PAINT_PPI = 72;

export const PAGE_SIZES = Object.freeze({
    A3: [842, 1191],
    A4: [595, 842],
    A5: [420, 595],
    Letter: [612, 792],
});

export function pageSize(size, orientation) {
    const [width, height] = PAGE_SIZES[size] ?? PAGE_SIZES.A4;
    return orientation === 'landscape' ? { width: height, height: width } : { width, height };
}

/** Spacing of each background pattern, in paint pixels, on the pixel grid. */
export const PATTERN_STEP = Object.freeze({ grid: 16, dots: 16, notebook: 24 });

/** Draws the background pattern on its own layer, so erasing never removes it. */
export function drawPattern(context, width, height, pattern, color) {
    context.clearRect(0, 0, width, height);
    context.fillStyle = color;
    switch (pattern) {
        case 'grid':
            for (let x = PATTERN_STEP.grid; x < width; x += PATTERN_STEP.grid) {
                context.fillRect(x, 0, 1, height);
            }

            for (let y = PATTERN_STEP.grid; y < height; y += PATTERN_STEP.grid) {
                context.fillRect(0, y, width, 1);
            }

            break;
        case 'dots':
            for (let x = PATTERN_STEP.dots; x < width; x += PATTERN_STEP.dots) {
                for (let y = PATTERN_STEP.dots; y < height; y += PATTERN_STEP.dots) {
                    context.fillRect(x, y, 1, 1);
                }
            }

            break;
        case 'notebook':
            for (let y = PATTERN_STEP.notebook * 3; y < height; y += PATTERN_STEP.notebook) {
                context.fillRect(0, y, width, 1);
            }

            // The margin rule, a little in from the left edge.
            context.fillRect(Math.round(width * 0.12), 0, 1, height);
            break;
    }
}
