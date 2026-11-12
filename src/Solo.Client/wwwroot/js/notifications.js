// OS notifications through the browser's Notification API. The desktop app's
// Electron renderer implements the same API natively, so this one file covers
// both. The tag is the notification's id: several tabs hearing the same
// notification replace one another's instead of stacking.
window.solo.notifications = {
    permission: () => 'Notification' in window ? Notification.permission : 'unsupported',

    request: async () => 'Notification' in window ? await Notification.requestPermission() : 'unsupported',

    timeZone: () => Intl.DateTimeFormat().resolvedOptions().timeZone,

    // Someone looking at the page gets the in-app snackbar, not a second alert.
    attended: () => document.visibilityState === 'visible' && document.hasFocus(),

    show(id, title, body, owner) {
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
};
