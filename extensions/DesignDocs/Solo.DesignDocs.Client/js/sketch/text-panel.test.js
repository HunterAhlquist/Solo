import test from 'node:test';
import assert from 'node:assert/strict';
import {panelPosition} from './text-panel.js';

const view={width:1000,height:600};
const panel={width:240,height:48};
const box=(left,top,width=200,height=40)=>({left,top,width,bottom:top+height});

test('the panel sits centred above the box', () => {
    const {x,y}=panelPosition(box(400,300),view,panel);

    assert.equal(x,500,'the centre of the box');
    assert.equal(y,300-48-10,'clear of the box by the gap');
});

// A box near the top of the view has no room above it, and a panel half off
// the top is worse than one below the text it formats.
test('the panel flips below a box with no room above it', () => {
    assert.equal(panelPosition(box(400,20),view,panel).y,20+40+10);
});

test('the panel stays inside the view at either edge', () => {
    assert.equal(panelPosition(box(0,300,40),view,panel).x,128,'half the panel plus the margin');
    assert.equal(panelPosition(box(980,300,40),view,panel).x,1000-120-8);
});

// Below and above both clip in a view shorter than the box, so the bottom
// clamp has to win rather than pushing the panel off the end.
test('a box taller than the view keeps the panel on screen', () => {
    const {y}=panelPosition(box(400,4,200,2000),{width:1000,height:300},panel);

    assert.ok(y>=8&&y<=300-48-8,`y ${y} is inside the view`);
});

// The box can be anywhere on a page far larger than the view, so "room above"
// is not the same as "on screen": panned past the bottom it had 800px of room
// above it in a 600px view, and the panel went with it.
test('a box panned out of the view keeps the panel on screen', () => {
    const below=panelPosition(box(400,800),view,panel);
    assert.ok(below.y<=view.height-panel.height-8,`y ${below.y} is off the bottom`);

    const over=panelPosition(box(400,-900),view,panel);
    assert.ok(over.y>=8,`y ${over.y} is off the top`);
});
