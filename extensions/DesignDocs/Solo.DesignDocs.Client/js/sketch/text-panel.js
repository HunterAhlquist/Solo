// Where the floating formatting panel sits.
//
// It follows the box being edited rather than parking beside the ink palette:
// the text can be anywhere on a page far larger than the view, and a panel in
// the corner makes the author look away from what they are formatting. All
// coordinates are relative to the viewport.
const EDGE=8;

// Max() on the far edge so a panel larger than the view pins to the near one
// rather than inverting the range.
const clamp=(value,low,high)=>Math.min(Math.max(value,low),Math.max(low,high));

/**
 * Centred above `rect`, flipped below it when there is no room above, and kept
 * inside `view` either way. `x` is the panel's centre, `y` its top edge.
 */
export function panelPosition(rect,view,panel,gap=10){
    const half=panel.width/2;
    const above=rect.top-gap-panel.height;
    // Both branches are clamped, not just the flipped one: the box can be
    // anywhere on a page far larger than the view, so a box panned off the
    // bottom has plenty of room "above" it — 800px down a 600px view — and
    // following it there takes the panel off screen with it.
    return {
        x:clamp(rect.left+rect.width/2,half+EDGE,view.width-half-EDGE),
        y:clamp(above>=EDGE?above:rect.bottom+gap,EDGE,view.height-panel.height-EDGE),
    };
}
