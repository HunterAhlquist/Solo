// Minimal interop surface: browser storage Blazor can't reach on its own.
// The one-shot pulse/shake animations are driven by adding and removing a CSS
// class from C#, so they need nothing here.
window.solo = {
    storage: {
        get: key => localStorage.getItem(key),
        set: (key, value) => localStorage.setItem(key, value),
        remove: key => localStorage.removeItem(key),
        getSession: key => sessionStorage.getItem(key),
        setSession: (key, value) => sessionStorage.setItem(key, value),
        removeSession: key => sessionStorage.removeItem(key),
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
    // pages in Electron, so the shell signs in through the system browser and
    // delivers the credential to `deliver` below — which trades it for a
    // long-lived Solo session (see `session`) so the desktop app stays signed
    // in past the credential's one-hour life.
    // -----------------------------------------------------------------------
    desktop: {
        available: () => !!window.soloDesktop,

        // Reuses the GIS callback plumbing: a browser-won credential flows to
        // the same .NET handler an in-page GIS sign-in would reach.
        init: dotnetRef => { window.solo.google._dotnet = dotnetRef; },

        beginSignIn: () => window.soloDesktop.beginSignIn(),

        // Called by the shell when the browser flow completes. Exchanges
        // before notifying, so .NET reads a live session the moment it hears.
        // The Google credential is kept purely for its display claims.
        async deliver(credential) {
            const google = window.solo.google;
            google._writeToken(credential);
            try {
                await window.solo.session.exchange(credential);
            } catch (err) {
                // Tell the sign-in UI why nothing happened — a silent return
                // leaves the user staring at the login screen.
                google._dotnet?.invokeMethodAsync('OnSignInFailed',
                    String(err?.message ?? err));
                return 'exchange-failed';
            }

            if (google._dotnet) {
                google._dotnet.invokeMethodAsync('OnCredentialReceived', credential);
                return 'notified';
            }

            return 'stored';
        },
    },

    // -----------------------------------------------------------------------
    // Solo API sessions (desktop only). A fresh Google credential buys a
    // long-lived `solo_` token from the instance; that token — not the Google
    // one — is what goes on the wire, and the server slides its expiry on use.
    // -----------------------------------------------------------------------
    session: {
        tokenKey: 'solo.session.token',

        async exchange(credential) {
            const response = await fetch('api/auth/sessions', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    Authorization: `Bearer ${credential}`,
                },
                body: JSON.stringify({ description: 'Solo Desktop' }),
            });
            // 404/405: the instance predates desktop sessions entirely.
            if (response.status === 404 || response.status === 405) {
                throw new Error('This Solo server does not support desktop sign-in yet — update the server and try again.');
            }

            if (!response.ok) {
                throw new Error(`The server refused the sign-in (HTTP ${response.status}).`);
            }

            const session = await response.json().catch(() => null);
            if (!session?.token) {
                throw new Error('The server answered, but not with a session — update the server and try again.');
            }

            this._write(session.token);
        },

        // Same "stay signed in" routing as the Google credential.
        _write(token) {
            const [into, outOf] = window.solo.google.remember()
                ? [localStorage, sessionStorage]
                : [sessionStorage, localStorage];
            into.setItem(this.tokenKey, token);
            outOf.removeItem(this.tokenKey);
        },

        token: () => sessionStorage.getItem(window.solo.session.tokenKey)
            ?? localStorage.getItem(window.solo.session.tokenKey),

        clear() {
            sessionStorage.removeItem(this.tokenKey);
            localStorage.removeItem(this.tokenKey);
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

        // Also the desktop shell's hand-off point: Electron cannot host
        // Google's sign-in pages, so Solo.Desktop completes GIS in the system
        // browser and injects the credential here (src/Solo.Desktop/main.js).
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
