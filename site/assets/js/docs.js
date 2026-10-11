// Behaviour for the docs chrome. Every page reads and navigates without it:
// this adds the drawer toggle, the appearance menu, the "On this page" list,
// heading links and the copy buttons.
(function () {
    'use strict';

    var root = document.documentElement;
    var SCHEME_KEY = 'solo-docs.colorScheme';
    var wide = window.matchMedia('(min-width: 960px)');

    // -- Drawer ---------------------------------------------------------------
    // On a wide screen the drawer starts open and the button folds it away. On
    // a narrow one it starts closed and slides over the page, behind a scrim.

    var toggle = document.getElementById('nav-toggle');
    var drawer = document.getElementById('drawer');
    var scrim = document.getElementById('scrim');

    function navIsOpen() {
        var state = root.dataset.nav;
        return state ? state === 'open' : wide.matches;
    }

    function setNav(open) {
        root.dataset.nav = open ? 'open' : 'closed';
        toggle.setAttribute('aria-expanded', String(open));
        scrim.hidden = !open || wide.matches;
    }

    if (toggle && drawer && scrim) {
        toggle.setAttribute('aria-expanded', String(navIsOpen()));
        toggle.addEventListener('click', function () { setNav(!navIsOpen()); });
        scrim.addEventListener('click', function () { setNav(false); });

        // Crossing the breakpoint goes back to that width's own default.
        wide.addEventListener('change', function () {
            delete root.dataset.nav;
            scrim.hidden = true;
            toggle.setAttribute('aria-expanded', String(navIsOpen()));
        });

        document.addEventListener('keydown', function (event) {
            if (event.key === 'Escape' && !wide.matches && navIsOpen()) {
                setNav(false);
                toggle.focus();
            }
        });

        var current = drawer.querySelector('[aria-current="page"]');
        if (current) {
            current.scrollIntoView({ block: 'nearest' });
        }
    }

    // -- Appearance -----------------------------------------------------------

    var menu = document.getElementById('scheme-menu');

    function storedScheme() {
        try {
            var value = localStorage.getItem(SCHEME_KEY);
            return value === 'light' || value === 'dark' ? value : 'system';
        } catch (e) {
            return 'system';
        }
    }

    // A screenshot with a light and a dark version picks one with a media
    // query, which follows the system. When the reader chose a scheme here,
    // the query is replaced with one that always or never matches.
    function syncPictures(scheme) {
        document.querySelectorAll('source[data-scheme-media]').forEach(function (source) {
            source.media = scheme === 'system'
                ? source.dataset.schemeMedia
                : (scheme === 'dark' ? 'all' : 'not all');
        });
    }

    function applyScheme(scheme) {
        if (scheme === 'system') {
            delete root.dataset.colorScheme;
        } else {
            root.dataset.colorScheme = scheme;
        }

        if (menu) {
            menu.querySelectorAll('[data-scheme]').forEach(function (item) {
                item.setAttribute('aria-checked', String(item.dataset.scheme === scheme));
            });
        }

        syncPictures(scheme);
    }

    applyScheme(storedScheme());

    if (menu) {
        menu.addEventListener('click', function (event) {
            var item = event.target.closest('[data-scheme]');
            if (!item) {
                return;
            }

            var scheme = item.dataset.scheme;
            try {
                if (scheme === 'system') {
                    localStorage.removeItem(SCHEME_KEY);
                } else {
                    localStorage.setItem(SCHEME_KEY, scheme);
                }
            } catch (e) {
                // Storage unavailable: the choice lasts for this page.
            }

            applyScheme(scheme);
        });

        document.addEventListener('click', function (event) {
            if (menu.open && !menu.contains(event.target)) {
                menu.open = false;
            }
        });

        document.addEventListener('keydown', function (event) {
            if (event.key === 'Escape' && menu.open) {
                menu.open = false;
                menu.querySelector('summary').focus();
            }
        });
    }

    // -- Tables ---------------------------------------------------------------
    // A wide table scrolls inside its own frame instead of widening the page.

    document.querySelectorAll('.prose table').forEach(function (table) {
        var wrap = document.createElement('div');
        wrap.className = 'table-wrap';
        table.parentNode.insertBefore(wrap, table);
        wrap.appendChild(table);
    });

    // -- Headings: a link on each, and the "On this page" list ------------------

    var toc = document.getElementById('toc');
    var headings = Array.prototype.slice.call(
        document.querySelectorAll('.page__body .prose h2[id], .page__body .prose h3[id]'));

    // Read before the link is added, so its "#" is not part of the label.
    var labels = headings.map(function (heading) { return heading.textContent.trim(); });

    headings.forEach(function (heading) {
        var anchor = document.createElement('a');
        anchor.className = 'heading-anchor';
        anchor.href = '#' + heading.id;
        anchor.textContent = '#';
        anchor.setAttribute('aria-label', 'Link to this section');
        heading.appendChild(anchor);
    });

    if (toc && headings.length > 1) {
        var list = document.createElement('ol');
        var links = {};

        headings.forEach(function (heading, index) {
            var item = document.createElement('li');
            var link = document.createElement('a');
            link.href = '#' + heading.id;
            link.textContent = labels[index];
            if (heading.tagName === 'H3') {
                item.className = 'toc__nested';
            }

            item.appendChild(link);
            list.appendChild(item);
            links[heading.id] = link;
        });

        toc.appendChild(list);
        toc.hidden = false;

        // The current section is the last heading above a line a little below
        // the app bar.
        var ticking = false;

        function markCurrent() {
            ticking = false;
            var line = 120;
            var currentId = headings[0].id;
            headings.forEach(function (heading) {
                if (heading.getBoundingClientRect().top <= line) {
                    currentId = heading.id;
                }
            });

            Object.keys(links).forEach(function (id) {
                if (id === currentId) {
                    links[id].setAttribute('aria-current', 'true');
                } else {
                    links[id].removeAttribute('aria-current');
                }
            });
        }

        window.addEventListener('scroll', function () {
            if (!ticking) {
                ticking = true;
                window.requestAnimationFrame(markCurrent);
            }
        }, { passive: true });

        markCurrent();
    }

    // -- Copy buttons on code blocks --------------------------------------------

    if (navigator.clipboard) {
        document.querySelectorAll('.prose div.highlighter-rouge').forEach(function (block) {
            var code = block.querySelector('pre');
            if (!code) {
                return;
            }

            var button = document.createElement('button');
            button.type = 'button';
            button.className = 'copy-button';
            button.setAttribute('aria-label', 'Copy');
            button.innerHTML =
                '<svg class="icon icon--small" aria-hidden="true"><use href="#i-content-copy"/></svg>';

            button.addEventListener('click', function () {
                navigator.clipboard.writeText(code.innerText.replace(/\n$/, '')).then(function () {
                    button.dataset.copied = '';
                    button.setAttribute('aria-label', 'Copied');
                    button.querySelector('use').setAttribute('href', '#i-check');
                    window.setTimeout(function () {
                        delete button.dataset.copied;
                        button.setAttribute('aria-label', 'Copy');
                        button.querySelector('use').setAttribute('href', '#i-content-copy');
                    }, 1500);
                });
            });

            block.appendChild(button);
        });
    }
})();
