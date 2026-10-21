// A right-pointing arrow drawn in the same square pixels as the icon's face:
// a dashed trail of three-pixel-tall blocks, then a solid head. Each trail
// segment takes the next shade, so the colour deepens towards the target.
function drawPixelArrow(svg, { pixel, shades }) {
  const HEIGHT = 9;              // rows, odd so the head has a centre row
  const MID = (HEIGHT - 1) / 2;
  const SEGMENT = 2;             // pixels per trail block
  const GAP = 1;                 // pixels between trail blocks
  const HEAD = MID + 1;          // columns in the head, tip included

  const rects = [];
  const add = (x, y, w, h, fill) => rects.push({ x, y, w, h, fill });

  // Trail: one block per shade but the last, which belongs to the head.
  let x = 0;
  for (const fill of shades.slice(0, -1)) {
    add(x, MID - 1, SEGMENT, 3, fill);
    x += SEGMENT + GAP;
  }

  // Head: full height at its base, narrowing one row each side per column.
  const head = shades.at(-1);
  add(x, MID - 1, 1, 3, head);   // joins the head to the trail
  x += 1;
  for (let col = 0; col < HEAD; col++) {
    add(x + col, col, 1, HEIGHT - 2 * col, head);
  }
  const width = x + HEAD;

  svg.setAttribute('width', width * pixel);
  svg.setAttribute('height', HEIGHT * pixel);
  svg.setAttribute('viewBox', `0 0 ${width} ${HEIGHT}`);
  svg.innerHTML = rects
    .map(r => `<rect x="${r.x}" y="${r.y}" width="${r.w}" height="${r.h}" fill="${r.fill}"/>`)
    .join('');
}
