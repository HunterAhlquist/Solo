// OS notifications through the browser's Notification API. The desktop app's
// Electron renderer implements the same API natively, so this one file covers
// both. The tag is the notification's id: several tabs hearing the same
// notification replace one another's instead of stacking.
//
// The iPhone and iPad app's web view has no Notification API. It offers the
// same calls as fixed messages instead (window.soloApp.notifications), plus one
// the API has no word for: scheduling deadline reminders ahead, so they show
// while the app is closed. A tap on one of its notifications comes back as a
// solo:notification-open event.
// A block, so `app` stays out of the page's global scope.
{
    const app = () => window.soloApp?.notifications;

    window.solo.notifications = {
        inApp: () => !!app(),

        permission: () => app()
            ? app().permission()
            : 'Notification' in window ? Notification.permission : 'unsupported',

        request: async () => app()
            ? await app().request()
            : 'Notification' in window ? await Notification.requestPermission() : 'unsupported',

        timeZone: () => Intl.DateTimeFormat().resolvedOptions().timeZone,

        // Someone looking at the page gets the in-app snackbar, not a second alert.
        attended: () => document.visibilityState === 'visible' && document.hasFocus(),

        show(id, title, body, owner) {
            if (app()) {
                return app().show(id, title, body ?? null).catch(() => false);
            }

            if (!('Notification' in window) || Notification.permission !== 'granted') {
                return false;
            }

            const notification = new Notification(title, { body: body ?? undefined, tag: id, icon: 'favicon.svg' });
            notification.onclick = () => {
                window.focus();
                notification.close();
                owner.invokeMethodAsync('OpenFromNotification', id);
            };
            return true;
        },

        canSchedule: () => !!app(),

        // Of these unread entries ({ key, reminder }), the keys of the ones
        // nothing on this device has said yet, now counted as said: the app
        // also announces new entries while it's closed, and shows deadline
        // reminders it scheduled, and the page shouldn't say either again.
        // An app from before reminder keys takes the device keys alone.
        claim: entries => {
            const keys = entries.map(e => e.key);
            const claimed = app()?.claimEntries ? app().claimEntries(entries)
                : app()?.claim ? app().claim(keys)
                : null;
            return claimed ? claimed.catch(() => keys) : keys;
        },

        // Replaces this instance's scheduled reminders; the app keeps as many of
        // the soonest as fit. Resolves with how many it scheduled.
        schedule: reminders => app()
            ? app().schedule(reminders.map(r => ({
                key: r.key, taskId: r.taskId, fireAt: Date.parse(r.fireAt), title: r.title, body: r.body ?? null,
            }))).catch(() => 0)
            : 0,

        // The store hears taps on the app's notifications and the page coming back
        // into view through these. In the app, the call at the end is what tells it
        // the client listens, which hands over a tap that launched it.
        attach(owner) {
            this.detach();
            this._open = event => {
                const detail = event.detail ?? {};
                if (typeof detail.id === 'string') {
                    owner.invokeMethodAsync('OpenFromNotification', detail.id);
                } else if (typeof detail.taskId === 'string') {
                    owner.invokeMethodAsync('OpenTaskFromNotification', detail.taskId);
                }
            };
            this._visible = () => {
                if (document.visibilityState === 'visible') {
                    owner.invokeMethodAsync('PageVisible');
                }
            };
            window.addEventListener('solo:notification-open', this._open);
            document.addEventListener('visibilitychange', this._visible);
            return app() ? app().permission().then(() => true, () => false) : false;
        },

        detach() {
            if (this._open) {
                window.removeEventListener('solo:notification-open', this._open);
                document.removeEventListener('visibilitychange', this._visible);
                this._open = this._visible = null;
            }
        },
    };
}
