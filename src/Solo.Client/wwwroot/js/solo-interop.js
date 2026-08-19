// Minimal interop surface: browser storage Blazor can't reach on its own, plus
// the handful of listeners at the foot of the file for input Blazor can't take
// at the rate the browser delivers it. The one-shot pulse/shake animations are
// driven by adding and removing a CSS class from C#, so they need nothing here.
window.solo = {
    storage: {
        get: key => localStorage.getItem(key),
        set: (key, value) => localStorage.setItem(key, value),
        remove: key => localStorage.removeItem(key),
        getSession: key => sessionStorage.getItem(key),
        setSession: (key, value) => sessionStorage.setItem(key, value),
        removeSession: key => sessionStorage.removeItem(key),
    },

    // Read synchronously at startup so a "match system" user never sees one
    // frame of the wrong scheme. MudThemeProvider watches for later changes.
    systemPrefersDark: () => window.matchMedia('(prefers-color-scheme: dark)').matches,

    // Rendered markdown link plumbing (see SoloMarkdown.razor). A capture-phase
    // listener at the bottom of this file records link clicks inside
    // .markdown-clickable containers, so click-to-edit hosts can tell a link
    // click from a click on the text around it, and swallows clicks inside
    // .markdown-locked so an editor's preview can't navigate a draft away.
    markdown: {
        _lastLinkClick: 0,

        wasLinkClick: () => Date.now() - window.solo.markdown._lastLinkClick < 400,
    },

    auth: {
        // The API expects the OIDC *ID token* (its ValidAudience is the client
        // id, and Google's access tokens aren't even JWTs). Blazor's token
        // provider only exposes the access token, so read the ID token straight
        // out of the store oidc-client-ts keeps it in. The key is
        // `oidc.user:{authority}:{clientId}`; scanning avoids re-deriving it.
        idToken: () => {
            for (const store of [sessionStorage, localStorage]) {
                for (let i = 0; i < store.length; i++) {
                    const key = store.key(i);
                    if (!key || !key.startsWith('oidc.user:')) {
                        continue;
                    }

                    try {
                        const token = JSON.parse(store.getItem(key)).id_token;
                        if (token) {
                            return token;
                        }
                    } catch {
                        // Corrupt entry — keep scanning.
                    }
                }
            }

            return null;
        },
    },

    // -----------------------------------------------------------------------
    // Desktop shell (Solo.Desktop). window.soloDesktop exists only inside the
    // Electron app, exposed by its preload. Google refuses to run its sign-in
    // pages in Electron, so the shell signs in through the system browser on
    // the instance's own origin; the instance verifies the sign-in, mints a
    // long-lived Solo session (see `session`), and the shell delivers it to
    // `deliverSession` below — no provider credential ever reaches the app.
    // -----------------------------------------------------------------------
    desktop: {
        _dotnet: null,

        available: () => !!window.soloDesktop,

        // Registers the .NET receiver the shell's delivery notifies.
        init: dotnetRef => { window.solo.desktop._dotnet = dotnetRef; },

        beginSignIn: () => window.soloDesktop.beginSignIn(),

        // The instance this shell is pointed at, or null on the web build where
        // the page origin already is the instance. Defensive about the bridge
        // being absent: this is called unconditionally at startup.
        instanceUrl: async () =>
            (window.soloDesktop?.instanceUrl ? await window.soloDesktop.instanceUrl() : null),

        // Called by the shell when the browser flow completes: the session is
        // already live server-side, so store it, then tell .NET.
        deliverSession(token, user) {
            window.solo.session.store(token, user);
            if (window.solo.desktop._dotnet) {
                window.solo.desktop._dotnet.invokeMethodAsync('OnDesktopSignedIn');
                return 'notified';
            }

            return 'stored';
        },

        // Called by the shell when the browser flow could not complete — a
        // silent return leaves the user staring at the login screen.
        signInFailed(message) {
            window.solo.desktop._dotnet?.invokeMethodAsync('OnSignInFailed', message);
        },
    },

    // -----------------------------------------------------------------------
    // Solo API sessions (desktop only). The browser sign-in buys a long-lived
    // `solo_` token from the instance; that token is what goes on the wire,
    // and the server slides its expiry on use. The profile delivered with it
    // is kept for display — desktop has no provider credential to read
    // claims from.
    // -----------------------------------------------------------------------
    session: {
        tokenKey: 'solo.session.token',
        userKey: 'solo.session.user',

        // Same "stay signed in" routing as the web credential: localStorage
        // survives restarts, sessionStorage scopes to the window.
        store(token, user) {
            const [into, outOf] = window.solo.google.remember()
                ? [localStorage, sessionStorage]
                : [sessionStorage, localStorage];
            into.setItem(this.tokenKey, token);
            outOf.removeItem(this.tokenKey);
            if (user) {
                into.setItem(this.userKey, JSON.stringify(user));
            }
            outOf.removeItem(this.userKey);
        },

        token: () => sessionStorage.getItem(window.solo.session.tokenKey)
            ?? localStorage.getItem(window.solo.session.tokenKey),

        user: () => sessionStorage.getItem(window.solo.session.userKey)
            ?? localStorage.getItem(window.solo.session.userKey),

        clear() {
            for (const key of [this.tokenKey, this.userKey]) {
                sessionStorage.removeItem(key);
                localStorage.removeItem(key);
            }
        },

        // Sign-out: revoke server-side, then forget locally regardless.
        async revoke() {
            const token = this.token();
            if (token) {
                try {
                    await fetch('api/auth/sessions/current', {
                        method: 'DELETE',
                        headers: { Authorization: `Bearer ${token}` },
                    });
                } catch {
                    // Instance unreachable — the server sweep will expire it.
                }
            }

            this.clear();
        },
    },

    // -----------------------------------------------------------------------
    // Google Identity Services. Google's "Web application" OAuth clients require
    // a client_secret at the token endpoint even with PKCE, which a browser
    // cannot hold — so we never touch the token endpoint. GIS returns the ID
    // token (the `credential`) straight from the authorization step, which is
    // the only thing the API ever wanted.
    // -----------------------------------------------------------------------
    google: {
        tokenKey: 'solo.google.idtoken',
        rememberKey: 'solo.google.remember',
        _dotnet: null,
        _loading: null,
        _renewResolve: null,

        // Loaded on demand: an instance signed in with Microsoft should never
        // fetch a script from Google.
        _load() {
            if (window.google?.accounts?.id) {
                return Promise.resolve();
            }

            this._loading ??= new Promise((resolve, reject) => {
                const script = document.createElement('script');
                script.src = 'https://accounts.google.com/gsi/client';
                script.async = true;
                script.onload = resolve;
                script.onerror = () => reject(new Error('Could not load Google Identity Services.'));
                document.head.appendChild(script);
            });

            return this._loading;
        },

        async init(clientId, dotnetRef) {
            await this._load();
            window.solo.google._dotnet = dotnetRef;
            window.google.accounts.id.initialize({
                client_id: clientId,
                callback: response => window.solo.google._onCredential(response.credential),
                auto_select: true,
                use_fedcm_for_prompt: true,
                cancel_on_tap_outside: false,
            });
        },

        _onCredential(credential) {
            window.solo.google._writeToken(credential);
            window.solo.google._dotnet?.invokeMethodAsync('OnCredentialReceived', credential);
            window.solo.google._renewResolve?.(credential);
        },

        // "Stay signed in" routes the credential to localStorage so it survives
        // a browser restart; otherwise sessionStorage scopes it to the tab.
        // Defaults to on: this is a small-team tool, and silent renewal already
        // keeps the session alive while the tab is open anyway.
        remember: () => localStorage.getItem(window.solo.google.rememberKey) !== '0',

        setRemember(value) {
            localStorage.setItem(this.rememberKey, value ? '1' : '0');
            const token = this.token();
            if (token) {
                this._writeToken(token);
            }
        },

        _writeToken(credential) {
            const [into, outOf] = this.remember()
                ? [localStorage, sessionStorage]
                : [sessionStorage, localStorage];
            into.setItem(this.tokenKey, credential);
            outOf.removeItem(this.tokenKey);
        },

        async renderButton(element, theme) {
            await this._load();
            window.google.accounts.id.renderButton(element, {
                type: 'standard',
                theme,
                size: 'large',
                text: 'continue_with',
                shape: 'pill',
                logo_alignment: 'left',
                width: 280,
            });
        },

        // Reading the stored token must not drag in the GIS script: the auth
        // state is queried on every page load, sign-in happens once.
        token: () => sessionStorage.getItem(window.solo.google.tokenKey)
            ?? localStorage.getItem(window.solo.google.tokenKey),

        // Silent renewal: ask GIS to re-issue a credential. With a live Google
        // session and auto_select, FedCM re-authenticates without a click.
        // Resolves the fresh credential, or null when the prompt is skipped or
        // nothing arrives in time (Google session gone, prompt suppressed).
        prompt() {
            return new Promise(resolve => {
                let settled = false;
                let timer = null;
                const settle = credential => {
                    if (settled) {
                        return;
                    }

                    settled = true;
                    clearTimeout(timer);
                    if (window.solo.google._renewResolve === settle) {
                        window.solo.google._renewResolve = null;
                    }

                    resolve(credential);
                };

                window.solo.google._renewResolve = settle;
                timer = setTimeout(() => settle(null), 15000);

                try {
                    window.google.accounts.id.prompt(notification => {
                        // Under FedCM most moment details are deprecated, but
                        // "skipped" still means no credential is coming.
                        if (notification?.isSkippedMoment?.()) {
                            settle(null);
                        }
                    });
                } catch {
                    settle(null);
                }
            });
        },

        // Dropping a dead credential is not signing out: leave auto-select
        // alone so the next sign-in can still be silent.
        retire() {
            sessionStorage.removeItem(this.tokenKey);
            localStorage.removeItem(this.tokenKey);
        },

        signOut() {
            this.retire();
            window.google?.accounts?.id?.disableAutoSelect();
        },
    },
};

// WebKit cancels a drag whose dragstart wrote nothing to the pasteboard, so on
// iPadOS the press falls through to the text-selection gesture and nothing ever
// drags. Blazor's DragEventArgs is a read-only snapshot — the hand-rolled drag
// sources (epic bands, sub-task rows) can't call setData from C# — so seed the
// payload here, in capture, before their @ondragstart runs. Only for a drag
// that carries nothing: a text, link or image drag already has its own data,
// and MudBlazor's cards set theirs (which is why those alone dragged on iPad).
// The type is deliberately '' rather than 'text/plain', as MudBlazor's is: a
// stray drop into a text field must not paste anything.
document.addEventListener('dragstart', e => {
    if (e.dataTransfer && e.dataTransfer.types.length === 0) {
        e.dataTransfer.setData('', 'solo-drag');
    }
}, true);

// ---------------------------------------------------------------------------
// Dragging a card to a column that is off-screen (the phone carousel).
//
// Below the md breakpoint each band's three status columns become a horizontal
// scroller, so the column you mean to drop into is usually not on screen when
// you pick a card up. Holding the card against either edge of the screen steps
// that band one column that way, the way holding a dragged file over a folder
// opens it; keep holding and it keeps stepping. Then drag back into the middle
// and let go — the edges are signage, not drop zones, so the drop still lands
// wherever the finger is.
//
// Up and down are left alone deliberately: dragging towards the top or bottom
// of the screen scrolls the page as it always did, which is how a card crosses
// into another swimlane.
//
// Here rather than in Blazor because dragover fires continuously for the whole
// drag — a round-trip per event would be a render per frame. The two indicator
// elements belong to Board.razor; this only toggles a class on them.
// ---------------------------------------------------------------------------
(() => {
    // How near the edge counts as held against it, and how long to hold. The
    // dwell is also the charge animation's duration in app.css.
    const EDGE_WIDTH = 44;
    const DWELL_MS = 1000;

    let dragging = false;
    let side = null;
    let timer = null;
    let pointerY = 0;

    const indicator = () => document.querySelector(`.board-edge--${side}`);

    // The band at this height, if it has anywhere left to scroll. Chosen by y
    // alone: at the edge of the screen the pointer is outside every band's box
    // horizontally, so hit-testing would find nothing. This is also what keeps
    // the whole feature off the desktop grid, where no band ever scrolls.
    const bandAt = y => [...document.querySelectorAll('.kanban-lane__zones')].find(zones => {
        const box = zones.getBoundingClientRect();
        // A little slack, so the gap between two bands isn't a dead spot.
        return y >= box.top - 8 && y <= box.bottom + 8 &&
            zones.scrollWidth > zones.clientWidth + 1;
    });

    // Where each column comes to rest, in the band's own scroll coordinates.
    // The same centring the CSS does, read back off the boxes rather than
    // recomputed from the widths.
    const restingPoints = zones => {
        const strip = zones.getBoundingClientRect();
        const furthest = zones.scrollWidth - zones.clientWidth;
        return [...zones.children].map(column => {
            const box = column.getBoundingClientRect();
            const left = box.left - strip.left + zones.scrollLeft;
            return Math.max(0, Math.min(left + box.width / 2 - zones.clientWidth / 2, furthest));
        });
    };

    // Steps one column; false when that edge has nothing left behind it.
    const step = (zones, direction) => {
        const points = restingPoints(zones);
        const nearest = points.reduce(
            (best, point, i) =>
                Math.abs(point - zones.scrollLeft) < Math.abs(points[best] - zones.scrollLeft) ? i : best,
            0);
        const target = nearest + direction;
        if (target < 0 || target >= points.length) {
            return false;
        }

        zones.scrollTo({ left: points[target], behavior: 'smooth' });
        return true;
    };

    // Lights the edge and starts the dwell. Removing the class and forcing a
    // reflow is what restarts the charge animation for a second step.
    const charge = () => {
        const element = indicator();
        if (element) {
            element.classList.remove('board-edge--armed');
            void element.offsetWidth;
            element.classList.add('board-edge--armed');
        }

        timer = setTimeout(fire, DWELL_MS);
    };

    function fire() {
        timer = null;
        const band = bandAt(pointerY);
        if (band && step(band, side === 'right' ? 1 : -1)) {
            charge();
            return;
        }

        // Nothing left that way. Go dark but stay latched to this edge, so the
        // indicator doesn't blink once a second while the finger rests there.
        indicator()?.classList.remove('board-edge--armed');
    }

    const disarm = () => {
        clearTimeout(timer);
        timer = null;
        if (side) {
            indicator()?.classList.remove('board-edge--armed');
            side = null;
        }
    };

    const arm = which => {
        if (which === side) {
            return;
        }

        disarm();
        // No steppable band at this height — say nothing rather than promise a
        // move that won't happen.
        if (!bandAt(pointerY)) {
            return;
        }

        side = which;
        charge();
    };

    // Only a card: the epic bands are their own drag, and reordering them is a
    // vertical business with no columns to step through.
    document.addEventListener('dragstart', e => {
        dragging = e.target instanceof Element && e.target.closest('.mud-drop-item') !== null;
    }, true);

    // Capture, and strictly a reader: preventDefault here would tell the browser
    // this is a drop target and swallow the real one.
    document.addEventListener('dragover', e => {
        if (!dragging) {
            return;
        }

        pointerY = e.clientY;
        if (e.clientX <= EDGE_WIDTH) {
            arm('left');
        } else if (e.clientX >= window.innerWidth - EDGE_WIDTH) {
            arm('right');
        } else {
            disarm();
        }
    }, true);

    for (const ending of ['dragend', 'drop']) {
        document.addEventListener(ending, () => {
            dragging = false;
            disarm();
        }, true);
    }
})();

// Capture phase, so it runs before Blazor's router and event delegation see
// the click (see window.solo.markdown above).
document.addEventListener('click', e => {
    const anchor = e.target instanceof Element ? e.target.closest('a') : null;
    if (!anchor) {
        return;
    }

    if (anchor.closest('.markdown-locked')) {
        e.preventDefault();
        e.stopPropagation();
        return;
    }

    if (anchor.closest('.markdown-clickable')) {
        window.solo.markdown._lastLinkClick = Date.now();
    }
}, true);
