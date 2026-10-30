// LOCAL VERIFY ONLY — not for commit. See .claude/skills/verify/SKILL.md.
// The session-token auth path is gated on window.soloDesktop existing at
// Blazor boot; this lets a local browser use a minted solo_ session instead of
// a Google round trip. Opt in with localStorage['solo.dev.desktop'] = '1'.
if (localStorage.getItem('solo.dev.desktop')) window.soloDesktop = { beginSignIn: () => {} };
