// electron-builder beforePack hook.
//
// mac.icon points at build/Solo.icon, which is staged from Icons/Solo.icon for
// whatever actool is installed (see stage-icon.mjs). Staging has to happen on
// every path into electron-builder, not just `npm run dist` — CI invokes
// `npx electron-builder` directly, and the staged copy is gitignored, so a
// fresh checkout has nothing to pack without this.
import { reportDropped, stageIcon } from './stage-icon.mjs';

export default async function beforePack(context) {
    if (context.electronPlatformName !== 'darwin') {
        return;
    }
    reportDropped(stageIcon());
}
