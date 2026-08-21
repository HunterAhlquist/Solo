// Runs in <head>, before anything paints.
//
// The boot splash is plain HTML in index.html: it is on screen for as long as
// the WASM runtime takes to start, which is well before the API has said what
// theme this instance uses. Left alone it would paint the hard-coded default and
// then jump — a black flash for anyone in light mode, or the wrong brand colour
// for anyone on a custom theme.
//
// So the app leaves the resolved splash colours in localStorage each time it
// runs (ThemeState.CacheBootStyleAsync), and this reads them back. First visit
// falls through to the CSS defaults, which is the one time a flash is unavoidable.
(function () {
    var root = document.documentElement;

    try {
        var preference = localStorage.getItem('solo.colorScheme');
        var prefersDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
        var dark = preference === 'Dark' || (preference !== 'Light' && prefersDark);
        var source = preference === 'Light' ? 'light' : preference === 'Dark' ? 'dark' : 'system';

        // Also read by app.css for the light-scheme splash defaults.
        root.dataset.colorScheme = dark ? 'dark' : 'light';
        root.style.colorScheme = dark ? 'dark' : 'light';

        // Preload exists before this head script. Tell the native window early
        // enough that glass and the boot splash agree on the first paint.
        window.soloDesktop?.setThemeSource?.(source);

        var cached = JSON.parse(localStorage.getItem('solo.bootStyle') || 'null');
        var colors = cached && (dark ? cached.dark : cached.light);
        if (colors) {
            // setProperty drops anything that isn't a valid declaration value,
            // so a corrupted entry degrades to the CSS default rather than
            // affecting the rule.
            root.style.setProperty('--solo-boot-bg', colors.bg);
            root.style.setProperty('--solo-boot-fg', colors.fg);
            root.style.setProperty('--solo-boot-surface', colors.surface);
        }
    } catch {
        // Storage unavailable or holding junk: the CSS defaults are correct enough.
    }
})();
