// Minimal interop surface: browser storage Blazor can't reach on its own.
// The one-shot pulse/shake animations are driven by adding and removing a CSS
// class from C#, so they need nothing here. Drag & drop has its own file.
window.solo = {
    storage: {
        get: key => localStorage.getItem(key),
        set: (key, value) => localStorage.setItem(key, value),
        remove: key => localStorage.removeItem(key),
        getSession: key => sessionStorage.getItem(key),
        setSession: (key, value) => sessionStorage.setItem(key, value),
        removeSession: key => sessionStorage.removeItem(key),
    },

    // The instance's display name, kept for js/theme-boot.js to put on the boot
    // splash and the tab before the next load has reached the API. Null when
    // the instance has no name of its own, which clears it.
    branding: {
        key: 'solo.displayName',

        remember(name) {
            if (name) {
                localStorage.setItem(this.key, name);
            } else {
                localStorage.removeItem(this.key);
            }
        },
    },

    // Keeps a keyboard-highlighted row in view, as the task picker's arrow keys
    // move it. MudBlazor's helper centres the row, which jumps the list each press.
    scrollIntoView: selector => document.querySelector(selector)?.scrollIntoView({ block: 'nearest' }),

    // Read synchronously at startup so a "match system" user never sees one
    // frame of the wrong scheme. solo.theme watches for later changes.
    systemPrefersDark: () => window.matchMedia('(prefers-color-scheme: dark)').matches,

    theme: {
        _systemQuery: window.matchMedia('(prefers-color-scheme: dark)'),
        _systemListener: null,
        _watchId: 0,

        // The web build has no desktop bridge, so this is intentionally a no-op
        // there. Electron validates the source again in its main process.
        setSource: source => window.soloDesktop?.setThemeSource?.(source),

        // data-color-scheme also controls the cached overscroll/boot colors.
        // Keep it current after startup, including Match system changes.
        // theme-color is the app bar's colour: the iPad app paints its status
        // bar with it (WKWebView.themeColor), and Safari its own bars.
        setResolvedScheme: (isDark, appBarColor) => {
            const scheme = isDark ? 'dark' : 'light';
            document.documentElement.dataset.colorScheme = scheme;
            document.documentElement.style.colorScheme = scheme;
            if (appBarColor) {
                let meta = document.querySelector('meta[name="theme-color"]');
                if (!meta) {
                    meta = document.createElement('meta');
                    meta.name = 'theme-color';
                    document.head.append(meta);
                }
                meta.content = appBarColor;
            }
        },

        // MudBlazor's helper does not consistently deliver Electron-driven
        // nativeTheme changes. Own this single media listener so macOS changes
        // and explicit desktop source changes reach ThemeState the same way.
        watchSystem: dotnetRef => {
            const theme = window.solo.theme;
            if (theme._systemListener) {
                theme._systemQuery.removeEventListener('change', theme._systemListener);
            }

            const watchId = ++theme._watchId;
            theme._systemListener = event =>
                dotnetRef.invokeMethodAsync('OnSystemDarkModeChanged', event.matches);
            theme._systemQuery.addEventListener('change', theme._systemListener);
            return watchId;
        },

        stopWatchingSystem: watchId => {
            const theme = window.solo.theme;
            if (watchId !== theme._watchId || !theme._systemListener) return;
            theme._systemQuery.removeEventListener('change', theme._systemListener);
            theme._systemListener = null;
        },
    },

    // Tells DayRollover when the person comes back to the page — window focus,
    // or the tab showing again — so a page left open overnight can redraw
    // around the new day. Focus and visibility both fire on a tab switch; the
    // second call finds the day already caught up.
    dayRollover: {
        _listener: null,
        _watchId: 0,

        // Remounting the page would drop an open dialog's page underneath it, a
        // held markdown draft (.markdown-locked, see SoloMarkdown.razor) or
        // whatever is half-typed in a focused field. Skip the call; the next
        // return asks again.
        _busy: () =>
            document.querySelector('.mud-dialog-container, .markdown-locked') !== null ||
            document.activeElement?.matches?.('input, textarea, select, [contenteditable]:not([contenteditable="false"])') === true,

        watch(dotnetRef) {
            const rollover = window.solo.dayRollover;
            rollover._remove();

            const watchId = ++rollover._watchId;
            rollover._listener = () => {
                if (document.visibilityState !== 'visible' || rollover._busy()) return;
                dotnetRef.invokeMethodAsync('OnPageReturned');
            };
            window.addEventListener('focus', rollover._listener);
            document.addEventListener('visibilitychange', rollover._listener);
            return watchId;
        },

        stop(watchId) {
            const rollover = window.solo.dayRollover;
            if (watchId === rollover._watchId) rollover._remove();
        },

        _remove() {
            const rollover = window.solo.dayRollover;
            if (!rollover._listener) return;
            window.removeEventListener('focus', rollover._listener);
            document.removeEventListener('visibilitychange', rollover._listener);
            rollover._listener = null;
        },
    },

    // Rendered markdown link plumbing (see SoloMarkdown.razor). A capture-phase
    // listener at the bottom of this file records link clicks inside
    // .markdown-clickable containers, so click-to-edit hosts can tell a link
    // click from a click on the text around it, and swallows clicks inside
    // .markdown-locked so an editor's preview can't navigate a draft away.
    markdown: {
        selection: root => {
            const input = root?.querySelector('textarea');
            return input ? input.value.slice(input.selectionStart, input.selectionEnd) : null;
        },
        insertAtCursor: (root, text, maxLength) => {
            const input = root?.querySelector('textarea');
            if (!input) return false;
            const start = input.selectionStart, end = input.selectionEnd;
            if (input.value.length - (end - start) + text.length > maxLength) return false;
            input.setRangeText(text, start, end, 'end');
            input.dispatchEvent(new Event('input', { bubbles: true }));
            input.focus();
            return true;
        },
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
    // The iPad app (Solo.Mobile) defines window.soloApp and signs in the same
    // way, in the system's sign-in sheet.
    // -----------------------------------------------------------------------
    desktop: {
        _dotnet: null,

        available: () => !!(window.soloDesktop || window.soloApp),

        // Registers the .NET receiver the shell's delivery notifies.
        init: dotnetRef => { window.solo.desktop._dotnet = dotnetRef; },

        // soloApp first: the verify skill's temporary login shim defines a
        // no-op soloDesktop, and the iPad app must not hand sign-in to that.
        beginSignIn: () => (window.soloApp ?? window.soloDesktop).beginSignIn(),

        // The shell's own list of instances: the desktop app's settings window,
        // or the iPad app's native sheet. Checked per member, so a shell built
        // before it existed just doesn't offer it.
        canOpenInstances: () => !!(window.soloApp?.openInstances || window.soloDesktop?.openInstances),
        openInstances: () => (window.soloApp?.openInstances ? window.soloApp : window.soloDesktop).openInstances(),

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
    // The host context menu (ISoloContextMenu). Inside the desktop shell it is
    // the operating system's own, through the preload's bridge. Anywhere else
    // ContextMenuHost draws it, and these place it and walk it by keyboard.
    // -----------------------------------------------------------------------
    contextMenu: {
        // { id } once the native menu closes, id null if nothing was chosen; or
        // null when there's no native menu to show, which leaves it to .NET.
        // Checked per call rather than via desktop.available(): a shell built
        // before the bridge had this still has the rest of it.
        native: async items => {
            if (!window.soloDesktop?.showContextMenu) {
                return null;
            }

            return { id: (await window.soloDesktop.showContextMenu(items)) ?? null };
        },

        // Opens at the point, flipped to the other side of it on either axis
        // where it would run off the viewport, as a native menu does, then
        // focuses the first item so the keyboard can take it from there.
        place(menu, x, y) {
            const margin = 8;
            const fit = (at, size, room) => at + size + margin <= room
                ? at
                : Math.max(margin, Math.min(at - size, room - size - margin));
            menu.style.left = `${fit(x, menu.offsetWidth, window.innerWidth)}px`;
            menu.style.top = `${fit(y, menu.offsetHeight, window.innerHeight)}px`;

            if (!menu._soloKeys) {
                menu._soloKeys = true;
                menu.addEventListener('keydown', e => {
                    const step = { ArrowDown: 1, ArrowUp: -1, Home: -Infinity, End: Infinity }[e.key];
                    if (step !== undefined) {
                        e.preventDefault();
                        window.solo.contextMenu._move(menu, step);
                    }
                });
            }

            window.solo.contextMenu._move(menu, -Infinity);
        },

        _move(menu, step) {
            const items = Array.from(menu.querySelectorAll('[role="menuitem"]:not([disabled])'));
            if (items.length === 0) {
                menu.focus();
                return;
            }

            const at = items.indexOf(document.activeElement);
            const next = step === -Infinity ? 0
                : step === Infinity ? items.length - 1
                : at < 0 ? 0
                : (at + step + items.length) % items.length;
            items[next].focus();
        },
    },

    // -----------------------------------------------------------------------
    // Device actions (ISoloDeviceActions). The iPad app turns an Apple Pencil
    // double-tap into `solo:device-action` with a generic name such as
    // `action_stylus-tool-swap` and dispatches it on the window from native
    // code. Nothing here calls back into the app, so the page offers the shell
    // a way in without being handed any native API of its own.
    // -----------------------------------------------------------------------
    deviceActions: {
        _dotnet: null,

        init(dotnetRef) {
            const actions = window.solo.deviceActions;
            if (!actions._dotnet) {
                window.addEventListener('solo:device-action', e => {
                    const id = e.detail?.id;
                    if (typeof id === 'string') {
                        actions._dotnet?.invokeMethodAsync('Dispatch', id);
                    }
                });
            }
            actions._dotnet = dotnetRef;
        },
    },

    // -----------------------------------------------------------------------
    // The live connection's wake-up: the page coming back into view, or the
    // browser back online, tells BoardHubClient to reconnect now instead of
    // at the next step of SignalR's backoff, which a few seconds with the
    // device locked can have pushed out to half a minute.
    // -----------------------------------------------------------------------
    live: {
        _dotnet: null,

        watchWake(dotnetRef) {
            const live = window.solo.live;
            if (!live._dotnet) {
                const wake = () => live._dotnet?.invokeMethodAsync('Wake');
                document.addEventListener('visibilitychange', () => {
                    if (document.visibilityState === 'visible') wake();
                });
                window.addEventListener('online', wake);
                window.addEventListener('pageshow', wake);
            }
            live._dotnet = dotnetRef;
        },
    },

    // -----------------------------------------------------------------------
    // Solo API sessions. A sign-in buys a long-lived `solo_` session from the
    // instance, and the server slides its expiry on use. The desktop app holds
    // it as a bearer token. The web app gets it as an HttpOnly cookie no script
    // can read, sent only beside the X-Solo-Client header (Solo.Api's
    // SessionCookie), so only the profile delivered with it is kept here, for
    // display and as the sign that a session exists.
    // -----------------------------------------------------------------------
    session: {
        tokenKey: 'solo.session.token',
        userKey: 'solo.session.user',

        // "Stay signed in" routing: localStorage survives restarts,
        // sessionStorage scopes to the window. The web passes no token.
        store(token, user) {
            const [into, outOf] = window.solo.google.remember()
                ? [localStorage, sessionStorage]
                : [sessionStorage, localStorage];
            if (token) {
                into.setItem(this.tokenKey, token);
            }
            outOf.removeItem(this.tokenKey);
            if (user) {
                into.setItem(this.userKey, JSON.stringify(user));
            }
            outOf.removeItem(this.userKey);
            this._handOver();
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
            this._handOver();
        },

        // The iPhone and iPad app keeps a copy of a remembered session for its
        // widgets to fetch with, and drops it with the session. One kept for
        // the window only stays the window's. On every change, and as the page
        // loads (below), so a sign-in from before the widgets reaches them.
        _handOver() {
            window.soloApp?.keepSession?.(localStorage.getItem(this.tokenKey));
        },

        // Web: trades a Google credential for the session cookie. Resolves the
        // profile as JSON, or null when the instance wouldn't mint a session
        // (an identity nobody invited) or couldn't be reached.
        async begin(credential) {
            try {
                const response = await fetch(
                    `api/auth/sessions/cookie?persistent=${window.solo.google.remember()}`, {
                        method: 'POST',
                        headers: { Authorization: `Bearer ${credential}`, 'Content-Type': 'application/json' },
                        body: JSON.stringify({ description: this._browserName() }),
                    });
                return response.ok ? this._keep(await response.json()) : null;
            } catch {
                return null;
            }
        },

        // Web: whether the instance still honours a session cookie this page
        // has no profile for. Same result shape as begin().
        async probe() {
            try {
                const response = await fetch('api/users/me', {
                    headers: { 'X-Solo-Client': '1' },
                    cache: 'no-store',
                });
                return response.ok ? this._keep(await response.json()) : null;
            } catch {
                return null;
            }
        },

        _keep(user) {
            this.store(null, user);
            return JSON.stringify(user);
        },

        // The signed-in devices list's label for this session.
        _browserName() {
            const agent = navigator.userAgent;
            const browser = /Edg\//.test(agent) ? 'Edge'
                : /Firefox\/|FxiOS/.test(agent) ? 'Firefox'
                : /Chrome\/|CriOS/.test(agent) ? 'Chrome'
                : /Safari\//.test(agent) ? 'Safari'
                : 'Browser';
            const os = /iPhone|iPad/.test(agent) ? 'iOS'
                : /Android/.test(agent) ? 'Android'
                : /Mac OS X/.test(agent) ? 'macOS'
                : /Windows/.test(agent) ? 'Windows'
                : /Linux/.test(agent) ? 'Linux'
                : null;
            return os ? `${browser} on ${os}` : browser;
        },

        // Sign-out: revoke server-side, then forget locally regardless. With
        // no token this is the web's cookie, which the response also clears.
        async revoke() {
            const token = this.token();
            try {
                await fetch('api/auth/sessions/current', {
                    method: 'DELETE',
                    headers: token ? { Authorization: `Bearer ${token}` } : { 'X-Solo-Client': '1' },
                });
            } catch {
                // Instance unreachable — the server sweep will expire it.
            }

            this.clear();
        },
    },

    // -----------------------------------------------------------------------
    // Google Identity Services. Google's "Web application" OAuth clients require
    // a client_secret at the token endpoint even with PKCE, which a browser
    // cannot hold — so we never touch the token endpoint. GIS returns the ID
    // token (the `credential`) straight from the authorization step, and the
    // instance trades it for a session (see `session`); it is never stored.
    // -----------------------------------------------------------------------
    google: {
        tokenKey: 'solo.google.idtoken',
        rememberKey: 'solo.google.remember',
        _dotnet: null,
        _loading: null,

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
            window.solo.google._dotnet?.invokeMethodAsync('OnCredentialReceived', credential);
        },

        // "Stay signed in": the session cookie and the profile survive a
        // browser restart, or go with the browser session. Defaults to on: this
        // is a small-team tool. Applies from the next sign-in.
        remember: () => localStorage.getItem(window.solo.google.rememberKey) !== '0',

        setRemember(value) {
            localStorage.setItem(this.rememberKey, value ? '1' : '0');
        },

        async renderButton(element, theme) {
            await this._load();
            // Cleared first: this also redraws an existing button when the
            // scheme changes.
            element.replaceChildren();
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

        // Where sign-ins from before sessions kept the credential. Read once,
        // to trade a live one for a session, then retired.
        token: () => sessionStorage.getItem(window.solo.google.tokenKey)
            ?? localStorage.getItem(window.solo.google.tokenKey),

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

(() => {
    const input = event => {
        if (!['mouse', 'touch', 'pen'].includes(event.pointerType)) return;
        if (document.documentElement.dataset.soloInput !== event.pointerType)
            document.documentElement.dataset.soloInput = event.pointerType;
    };
    for (const type of ['pointerover', 'pointermove', 'pointerdown'])
        document.addEventListener(type, input, { capture: true, passive: true });
    document.addEventListener('keydown', event => {
        if (event.key === 'Tab') document.documentElement.dataset.soloInput = 'keyboard';
    }, true);
})();

window.solo.session._handOver();
