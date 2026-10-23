// The widgets live preview draws in place of markdown it hides: images,
// image stacks, sketches, task chips, list bullets, checkboxes and rules.
// Everything is built with DOM calls and textContent. Note text never reaches
// innerHTML, so HTML in a note (common in imported Obsidian notes) stays text.

import { WidgetType } from '@codemirror/view';
import { cardAngle } from '../images/cache.js';
import { openLightbox } from '../images/lightbox.js';

/** One image: a paper card on its own line, or a small one inline beside text. */
export class ImageWidget extends WidgetType {
    constructor(image, alt, inline, host) {
        super();
        this.image = image;
        this.alt = alt;
        this.inline = inline;
        this.host = host;
    }

    eq(other) {
        return other.image.id === this.image.id && other.image.version === this.image.version &&
            other.inline === this.inline && other.alt === this.alt;
    }

    toDOM() {
        const card = document.createElement('span');
        card.className = `dd-image ${this.inline ? 'dd-image--inline' : 'dd-image--card'}`;
        card.style.setProperty('--dd-angle', `${cardAngle(this.image.id)}deg`);
        card.setAttribute('role', 'button');
        card.tabIndex = 0;
        card.title = this.alt || this.image.path;
        const img = document.createElement('img');
        img.alt = this.alt || this.image.path;
        img.draggable = false;
        if (this.image.width && this.image.height) {
            img.width = this.image.width;
            img.height = this.image.height;
        }

        card.append(img);
        const url = this.host.imageUrl(this.image);
        url.then(src => { img.src = src; }, () => card.classList.add('dd-image--missing'));
        const open = e => {
            e.preventDefault();
            e.stopPropagation();
            openLightbox([url], 0, [this.image.path]);
        };
        card.addEventListener('mousedown', e => e.preventDefault());
        card.addEventListener('click', open);
        card.addEventListener('keydown', e => (e.key === 'Enter' || e.key === ' ') && open(e));
        return card;
    }

    ignoreEvent() {
        return true;
    }
}

/** Two or more images with only whitespace between them: a fanned stack that opens a carousel. */
export class StackWidget extends WidgetType {
    constructor(images, host) {
        super();
        this.images = images;
        this.host = host;
    }

    eq(other) {
        return other.images.length === this.images.length &&
            other.images.every((image, i) => image.id === this.images[i].id && image.version === this.images[i].version);
    }

    toDOM() {
        const stack = document.createElement('span');
        stack.className = 'dd-stack';
        stack.setAttribute('role', 'button');
        stack.tabIndex = 0;
        stack.title = `${this.images.length} images`;
        const urls = this.images.map(image => this.host.imageUrl(image));
        this.images.slice(0, 4).forEach((image, i) => {
            const card = document.createElement('span');
            card.className = 'dd-stack__card';
            card.style.setProperty('--dd-angle', `${(i - 1) * 4 + Number(cardAngle(image.id)) / 2}deg`);
            card.style.setProperty('--dd-shift', `${i * 14}px`);
            const img = document.createElement('img');
            img.alt = image.path;
            img.draggable = false;
            urls[i].then(src => { img.src = src; }, () => card.classList.add('dd-image--missing'));
            card.append(img);
            stack.append(card);
        });
        const count = document.createElement('span');
        count.className = 'dd-stack__count';
        count.textContent = String(this.images.length);
        stack.append(count);

        const open = e => {
            e.preventDefault();
            e.stopPropagation();
            openLightbox(urls, 0, this.images.map(image => image.path));
        };
        stack.addEventListener('mousedown', e => e.preventDefault());
        stack.addEventListener('click', open);
        stack.addEventListener('keydown', e => (e.key === 'Enter' || e.key === ' ') && open(e));
        return stack;
    }

    ignoreEvent() {
        return true;
    }
}

/** A sketch embed: a live, read-only view of the sketch, and a button that opens its editor. */
export class SketchWidget extends WidgetType {
    constructor(sketch, host) {
        super();
        this.sketch = sketch;
        this.host = host;
    }

    eq(other) {
        return other.sketch.id === this.sketch.id && other.sketch.version === this.sketch.version;
    }

    toDOM() {
        const frame = document.createElement('span');
        frame.className = 'dd-sketch-embed';
        const view = document.createElement('span');
        view.className = 'dd-sketch-embed__view';
        const label = document.createElement('span');
        label.className = 'dd-sketch-embed__label';
        label.textContent = this.sketch.name;
        frame.append(view, label);
        if (this.host.canOpenSketch()) {
            const edit = document.createElement('button');
            edit.type = 'button';
            edit.className = 'dd-sketch-embed__edit';
            edit.textContent = this.host.canWrite() ? 'Draw' : 'Open';
            edit.addEventListener('mousedown', e => e.preventDefault());
            edit.addEventListener('click', e => {
                e.preventDefault();
                e.stopPropagation();
                this.host.openSketch(this.sketch.id);
            });
            frame.append(edit);
        }

        frame.ddPreview = this.host.mountSketchPreview(view, this.sketch);
        return frame;
    }

    destroy(dom) {
        dom.ddPreview?.destroy();
    }

    ignoreEvent() {
        return true;
    }
}

/** A link to a task, as a chip with its current serial and name. */
export class TaskChipWidget extends WidgetType {
    constructor(taskId, written, task, host) {
        super();
        this.taskId = taskId;
        this.written = written;
        this.task = task;
        this.host = host;
    }

    eq(other) {
        return other.taskId === this.taskId && other.written === this.written &&
            other.task?.serial === this.task?.serial && other.task?.name === this.task?.name &&
            other.task?.isFinished === this.task?.isFinished;
    }

    toDOM() {
        const chip = document.createElement('span');
        chip.className = 'dd-task-chip';
        chip.setAttribute('role', 'button');
        chip.tabIndex = 0;
        if (this.task) {
            const serial = document.createElement('span');
            serial.className = 'dd-task-chip__serial';
            serial.textContent = this.task.serial;
            const name = document.createElement('span');
            name.className = 'dd-task-chip__name';
            name.textContent = this.task.name;
            chip.append(serial, name);
            chip.classList.toggle('dd-task-chip--finished', !!this.task.isFinished);
        } else {
            chip.textContent = this.written;
            chip.classList.add('dd-task-chip--unknown');
        }

        const open = e => {
            e.preventDefault();
            e.stopPropagation();
            this.host.openTask(this.taskId);
        };
        chip.addEventListener('mousedown', e => e.preventDefault());
        chip.addEventListener('click', open);
        chip.addEventListener('keydown', e => (e.key === 'Enter' || e.key === ' ') && open(e));
        return chip;
    }

    ignoreEvent() {
        return true;
    }
}

export class BulletWidget extends WidgetType {
    eq() {
        return true;
    }

    toDOM() {
        const bullet = document.createElement('span');
        bullet.className = 'dd-bullet';
        bullet.textContent = '•';
        return bullet;
    }
}

/** A task list's checkbox. Ticking it rewrites the marker, so it syncs like any edit. */
export class CheckboxWidget extends WidgetType {
    constructor(checked, at, editable) {
        super();
        this.checked = checked;
        this.at = at;
        this.editable = editable;
    }

    eq(other) {
        return other.checked === this.checked && other.at === this.at && other.editable === this.editable;
    }

    toDOM(view) {
        const box = document.createElement('input');
        box.type = 'checkbox';
        box.className = 'dd-checkbox';
        box.checked = this.checked;
        box.disabled = !this.editable;
        box.addEventListener('mousedown', e => e.preventDefault());
        box.addEventListener('click', e => {
            e.preventDefault();
            if (!this.editable) {
                return;
            }

            // The marker is "[ ]" or "[x]"; the character inside is at + 1.
            view.dispatch({ changes: { from: this.at + 1, to: this.at + 2, insert: this.checked ? ' ' : 'x' } });
        });
        return box;
    }

    ignoreEvent() {
        return false;
    }
}

export class RuleWidget extends WidgetType {
    eq() {
        return true;
    }

    toDOM() {
        const rule = document.createElement('span');
        rule.className = 'dd-rule';
        return rule;
    }
}
