// electron-builder beforePack hook.
//
// mac.icon points at build/Solo.icon, staged from the channel's source icon for
// whatever actool is installed (see stage-icon.mjs). Staging has to happen on
// every path into electron-builder, not just `npm run dist` — CI invokes
// `npx electron-builder` directly, and the staged copy is gitignored, so a
// fresh checkout has nothing to pack without this.
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { reportDropped, stageIcon } from './stage-icon.mjs';
import identity from '../main/app-identity.js';

export default async function beforePack(context) {
    if (context.electronPlatformName !== 'darwin') {
        return;
    }
    const source = path.join(path.dirname(fileURLToPath(import.meta.url)), '../../../Icons',
        identity.identityForVersion(context.packager.appInfo.version).iconSource);
    reportDropped(stageIcon(undefined, source));
}
