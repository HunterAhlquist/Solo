// Live preview: one view for reading and editing, as in Obsidian. Markdown
// symbols are hidden except on the lines holding this editor's own selection,
// and embeds become images, stacks, sketches and task chips.
//
// Decorations belong to this view's state alone, so another person's cursor
// never reveals symbols here. They come from a state field rather than a view
// plugin because a stack of images can replace line breaks, which only a
// field may do (https://codemirror.net/docs/ref/#view.Decoration^replace).

import { StateEffect, StateField } from '@codemirror/state';
import { Decoration, EditorView } from '@codemirror/view';
import { syntaxTree } from '@codemirror/language';
import { isSafeUrl, slug } from './markdown.js';
import { isImageReference, isSketchReference, parseWikiLink } from '../links/obsidian.js';
import {
    BulletWidget, CheckboxWidget, ImageWidget, RuleWidget, SketchWidget, StackWidget, TaskChipWidget,
} from './widgets.js';

/** The editor gained or lost focus; symbols show only while it has it. */
export const setFocus = StateEffect.define();

/** Something decorations depend on changed outside the text: the tree, task names, write access. */
export const refreshPreview = StateEffect.define();

const focusField = StateField.define({
    create: () => false,
    update(focused, tr) {
        for (const effect of tr.effects) {
            if (effect.is(setFocus)) {
                focused = effect.value;
            }
        }

        return focused;
    },
});

export const TASK_LINK = /^\/docs\/task\/([0-9a-f]{32}|[0-9a-f-]{36})\/?$/i;
const WIKI = /(!?)\[\[([^\[\]\n]+?)\]\]/g;
const CODE = new Set(['InlineCode', 'FencedCode', 'CodeBlock', 'CodeText', 'HTMLBlock', 'CommentBlock']);
const HEADING = /^(ATXHeading|SetextHeading)(\d)$/;

/**
 * @param context {{
 *   index: () => import('../links/obsidian.js').VaultIndex,
 *   noteId: string,
 *   task: (id: string) => object|undefined,
 *   wantTask: (id: string) => void,
 *   canWrite: () => boolean,
 *   host: object,
 * }}
 */
export function livePreview(context) {
    const decorations = StateField.define({
        create: state => build(state, context),
        update(value, tr) {
            const changed = tr.docChanged || tr.selection ||
                tr.effects.some(e => e.is(setFocus) || e.is(refreshPreview)) ||
                syntaxTree(tr.state) !== syntaxTree(tr.startState);
            return changed ? build(tr.state, context) : value;
        },
        provide: field => EditorView.decorations.from(field),
    });

    return [
        focusField,
        EditorView.focusChangeEffect.of((_, focusing) => setFocus.of(focusing)),
        decorations,
        EditorView.domEventHandlers({
            mousedown(event, view) {
                return follow(event, view, context);
            },
        }),
    ];
}

/** Follows a rendered link on a press, before the press can move the cursor onto it and reveal its source. */
function follow(event, view, context) {
    if (event.button !== 0 || event.metaKey || event.ctrlKey || event.altKey || event.shiftKey) {
        return false;
    }

    const target = event.target instanceof Element ? event.target.closest('[data-dd-href], [data-dd-note]') : null;
    if (!target || !view.dom.contains(target)) {
        return false;
    }

    event.preventDefault();
    if (target.dataset.ddNote) {
        context.host.openNote(target.dataset.ddNote, target.dataset.ddHeading ?? null);
    } else if (target.dataset.ddHref) {
        context.host.navigate(target.dataset.ddHref);
    } else if (target.dataset.ddUnresolved) {
        context.host.unresolved(target.dataset.ddUnresolved);
    }

    return true;
}

function build(state, context) {
    const doc = state.doc;
    const tree = syntaxTree(state);
    const index = context.index();
    const focused = state.field(focusField);

    // Lines holding this view's selection, where the source shows as written.
    const activeLines = new Set();
    if (focused) {
        for (const range of state.selection.ranges) {
            for (let line = doc.lineAt(range.from).number; line <= doc.lineAt(range.to).number; line++) {
                activeLines.add(line);
            }
        }
    }

    const isActive = (from, to) => {
        for (let line = doc.lineAt(from).number; line <= doc.lineAt(to).number; line++) {
            if (activeLines.has(line)) {
                return true;
            }
        }

        return false;
    };
    const inCode = pos => {
        for (let node = tree.resolveInner(pos, 1); node; node = node.parent) {
            if (CODE.has(node.name)) {
                return true;
            }
        }

        return false;
    };

    const ranges = [];
    const replaced = [];
    const replace = (from, to, widget, block = false) => {
        if (from >= to) {
            return;
        }

        ranges.push(Decoration.replace({ widget, block }).range(from, to));
        replaced.push([from, to]);
    };
    const covered = (from, to) => replaced.some(([a, b]) => from >= a && to <= b);
    const hide = (from, to) => {
        if (from < to && !covered(from, to)) {
            ranges.push(Decoration.replace({}).range(from, to));
        }
    };
    const mark = (from, to, spec) => {
        if (from < to && !covered(from, to)) {
            ranges.push(Decoration.mark(spec).range(from, to));
        }
    };
    const lineClass = (pos, className) => {
        if (!covered(pos, pos)) {
            ranges.push(Decoration.line({ class: className }).range(doc.lineAt(pos).from));
        }
    };

    // Pass 1: embeds, which replace whole stretches, grouped by paragraph so
    // images side by side become one stack.
    const embeds = [];
    tree.iterate({
        enter(node) {
            if (node.name !== 'Image') {
                return;
            }

            const url = node.node.getChild('URL');
            const reference = url ? doc.sliceString(url.from, url.to) : '';
            const image = reference ? index.resolveImage(reference, true) : null;
            const altFrom = node.from + 2;
            const alt = doc.sliceString(altFrom, Math.max(altFrom, findLabelEnd(node.node) ?? altFrom));
            embeds.push({ from: node.from, to: node.to, image, alt, missing: image ? null : reference });
        },
    });

    const wikiLinks = [];
    for (let line = 1; line <= doc.lines; line++) {
        const { from, text } = doc.line(line);
        for (const match of text.matchAll(WIKI)) {
            const start = from + match.index;
            const end = start + match[0].length;
            if (inCode(start)) {
                continue;
            }

            const link = parseWikiLink(match[2]);
            if (match[1] === '!') {
                if (isSketchReference(link.target)) {
                    embeds.push({ from: start, to: end, sketch: index.resolveSketch(link.target), missing: link.target, isSketch: true });
                } else if (isImageReference(link.target)) {
                    const image = index.resolveImage(link.target);
                    embeds.push({ from: start, to: end, image, alt: link.alias ?? '', missing: image ? null : link.target });
                } else {
                    // A note embedded in a note shows as a link to it; transclusion can come later.
                    wikiLinks.push({ from: start, to: end, link, embed: true });
                }
            } else {
                wikiLinks.push({ from: start, to: end, link, embed: false });
            }
        }
    }

    const byParagraph = new Map();
    for (const embed of embeds) {
        let paragraph = null;
        for (let node = tree.resolveInner(embed.from, 1); node; node = node.parent) {
            if (node.name === 'Paragraph') {
                paragraph = node;
                break;
            }
        }

        const key = paragraph ? paragraph.from : -1 - embed.from;
        if (!byParagraph.has(key)) {
            byParagraph.set(key, { paragraph, embeds: [] });
        }

        byParagraph.get(key).embeds.push(embed);
    }

    for (const { paragraph, embeds: group } of byParagraph.values()) {
        group.sort((a, b) => a.from - b.from);
        const images = group.filter(e => e.image);
        const from = paragraph ? paragraph.from : group[0].from;
        const to = paragraph ? paragraph.to : group[group.length - 1].to;
        let rest = doc.sliceString(from, to);
        for (const embed of [...group].reverse()) {
            rest = rest.slice(0, embed.from - from) + rest.slice(embed.to - from);
        }

        const alone = rest.trim() === '' && images.length === group.length;
        if (alone && images.length >= 2 && !isActive(group[0].from, group[group.length - 1].to)) {
            replace(group[0].from, group[group.length - 1].to,
                new StackWidget(images.map(e => e.image), context.host));
            continue;
        }

        for (const embed of group) {
            if (isActive(embed.from, embed.to)) {
                continue;
            }

            if (embed.image) {
                replace(embed.from, embed.to, new ImageWidget(embed.image, embed.alt, !alone, context.host));
            } else if (embed.sketch) {
                replace(embed.from, embed.to, new SketchWidget(embed.sketch, context.host));
            } else {
                mark(embed.from, embed.to, {
                    class: 'dd-missing',
                    attributes: { title: `${embed.missing} isn't in this design doc.` },
                });
            }
        }
    }

    // Pass 2: the syntax tree's own nodes.
    tree.iterate({
        enter(node) {
            const name = node.name;
            const heading = HEADING.exec(name);
            if (heading) {
                const level = Number(heading[2]);
                for (let pos = node.from; pos <= node.to;) {
                    const line = doc.lineAt(pos);
                    if (!(name.startsWith('Setext') && line.to === node.to && line.from !== node.from)) {
                        lineClass(line.from, `dd-h dd-h${level}`);
                    }

                    pos = line.to + 1;
                }

                if (!isActive(node.from, node.to)) {
                    for (const child of children(node.node, 'HeaderMark')) {
                        // The space after an opening '#' run goes with it.
                        const after = doc.sliceString(child.to, child.to + 1);
                        hide(child.from, child.to + (after === ' ' && child.from === node.from ? 1 : 0));
                    }
                }

                return;
            }

            switch (name) {
                case 'Emphasis':
                case 'StrongEmphasis':
                case 'Strikethrough':
                case 'InlineCode': {
                    const className = { Emphasis: 'dd-em', StrongEmphasis: 'dd-strong', Strikethrough: 'dd-strike', InlineCode: 'dd-code' }[name];
                    mark(node.from, node.to, { class: className });
                    if (!isActive(node.from, node.to)) {
                        for (const child of children(node.node, name === 'InlineCode' ? 'CodeMark' : name === 'Strikethrough' ? 'StrikethroughMark' : 'EmphasisMark')) {
                            hide(child.from, child.to);
                        }
                    }

                    return name !== 'InlineCode';
                }

                case 'Link':
                    link(node.node);
                    return false;

                case 'Image':
                    return false;

                case 'Autolink':
                case 'URL': {
                    if (node.node.parent?.name === 'Link' || node.node.parent?.name === 'Image') {
                        return false;
                    }

                    const url = doc.sliceString(node.from, node.to).replace(/^<|>$/g, '');
                    const href = isSafeUrl(url) && /^(https?:|mailto:)/i.test(url) ? url : null;
                    mark(node.from, node.to, href
                        ? { class: 'dd-link', attributes: activeOr(node, { 'data-dd-href': href }) }
                        : { class: 'dd-link dd-link--unsafe' });
                    return false;
                }

                case 'Blockquote':
                    for (let pos = node.from; pos <= node.to;) {
                        const line = doc.lineAt(pos);
                        lineClass(line.from, 'dd-quote');
                        pos = line.to + 1;
                    }

                    return true;

                case 'QuoteMark':
                    if (!isActive(node.from, node.to)) {
                        const after = doc.sliceString(node.to, node.to + 1);
                        hide(node.from, node.to + (after === ' ' ? 1 : 0));
                    }

                    return false;

                case 'ListMark': {
                    const text = doc.sliceString(node.from, node.to);
                    const task = node.node.nextSibling?.name === 'Task';
                    if (!isActive(node.from, node.to) && !task && /^[-*+]$/.test(text)) {
                        replace(node.from, node.to, new BulletWidget());
                    }

                    return false;
                }

                case 'TaskMarker': {
                    if (!isActive(node.from, node.to)) {
                        const checked = /x/i.test(doc.sliceString(node.from, node.to));
                        // The list's own '-' goes too, so the checkbox stands in for both.
                        const listMark = node.node.parent?.prevSibling;
                        const from = listMark?.name === 'ListMark' ? listMark.from : node.from;
                        replace(from, node.to, new CheckboxWidget(checked, node.from, context.canWrite()));
                    }

                    return false;
                }

                case 'FencedCode':
                case 'CodeBlock':
                    for (let pos = node.from; pos <= node.to;) {
                        const line = doc.lineAt(pos);
                        const fence = name === 'FencedCode' && (line.from === node.from || line.to === node.to);
                        lineClass(line.from, fence ? 'dd-codeblock dd-codeblock--fence' : 'dd-codeblock');
                        pos = line.to + 1;
                    }

                    return false;

                case 'HorizontalRule':
                    if (!isActive(node.from, node.to)) {
                        replace(node.from, node.to, new RuleWidget());
                    }

                    return false;

                case 'HTMLTag':
                case 'HTMLBlock':
                case 'CommentBlock':
                case 'Comment':
                    // Shown as the text it is. Nothing in a note is ever parsed as HTML.
                    mark(node.from, node.to, { class: 'dd-html' });
                    return false;
            }

            return true;
        },
    });

    function link(node) {
        const url = node.getChild('URL');
        const href = url ? doc.sliceString(url.from, url.to) : null;
        const labelFrom = node.from + 1;
        const labelTo = findLabelEnd(node) ?? labelFrom;
        const active = isActive(node.from, node.to);
        const task = href ? TASK_LINK.exec(href) : null;
        if (task) {
            const id = task[1].replace(/-/g, '').toLowerCase();
            context.wantTask(id);
            if (!active) {
                replace(node.from, node.to,
                    new TaskChipWidget(id, doc.sliceString(labelFrom, labelTo), context.task(id), context.host));
                return;
            }
        }

        if (active) {
            mark(labelFrom, labelTo, { class: 'dd-link' });
            return;
        }

        hide(node.from, labelFrom);
        hide(labelTo, node.to);
        mark(labelFrom, labelTo, href !== null && isSafeUrl(href)
            ? { class: 'dd-link', attributes: { 'data-dd-href': href, title: href } }
            : { class: 'dd-link dd-link--unsafe' });
    }

    function activeOr(node, attributes) {
        return isActive(node.from, node.to) ? {} : attributes;
    }

    // Wiki links last: they live inside paragraphs the tree left as plain text.
    for (const { from, to, link: target, embed } of wikiLinks) {
        if (covered(from, to)) {
            continue;
        }

        const note = index.resolveNote(target.target, context.noteId);
        if (isActive(from, to)) {
            mark(from, to, { class: 'dd-wikilink-source' });
            continue;
        }

        const label = target.alias ?? (target.target
            ? (target.heading ? `${target.target} › ${target.heading}` : target.target)
            : target.heading ?? '');
        // The visible text is the link's own: alias when given, else target and heading.
        const innerFrom = from + (embed ? 3 : 2);
        const innerTo = to - 2;
        const attributes = note
            ? { 'data-dd-note': note.id, ...(target.heading ? { 'data-dd-heading': slug(target.heading) } : {}) }
            : { 'data-dd-unresolved': target.target, title: `${target.target} doesn't exist yet.` };
        const className = note ? 'dd-link dd-wikilink' : 'dd-link dd-wikilink dd-unresolved';

        if (target.alias) {
            const pipe = doc.sliceString(innerFrom, innerTo).indexOf('|');
            hide(from, innerFrom + pipe + 1);
            mark(innerFrom + pipe + 1, innerTo, { class: className, attributes });
        } else {
            hide(from, innerFrom);
            mark(innerFrom, innerTo, { class: className, attributes: { ...attributes, 'aria-label': label } });
        }

        hide(innerTo, to);
    }

    return Decoration.set(ranges, true);
}

function* children(node, name) {
    for (let child = node.firstChild; child; child = child.nextSibling) {
        if (child.name === name) {
            yield child;
        }
    }
}

/** Where a link's or image's label ends: the start of the ']' that closes it. */
function findLabelEnd(node) {
    let marks = 0;
    for (let child = node.firstChild; child; child = child.nextSibling) {
        if (child.name === 'LinkMark') {
            marks++;
            if (marks === 2) {
                return child.from;
            }
        }
    }

    return null;
}
