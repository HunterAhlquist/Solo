// Stages Icons/Solo.icon and Icons/Solo Preview.icon into ios/App/Icons, the
// git-ignored copies the Xcode project compiles, with any Icon Composer feature
// the installed actool can't compile dropped. An Xcode that predates a feature
// the icons declare (Xcode 26 and "refractivity") crashes actool outright rather
// than drawing without it; the desktop app's stage-icon.mjs explains the
// details, and this reuses its probe so the two can't drift apart.
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { stageIcon, reportDropped } from '../../Solo.Desktop/scripts/stage-icon.mjs';

const mobileDir = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const repoRoot = path.join(mobileDir, '..', '..');

for (const name of ['Solo.icon', 'Solo Preview.icon']) {
    const dest = path.join(mobileDir, 'ios', 'App', 'Icons', name);
    reportDropped(stageIcon(dest, path.join(repoRoot, 'Icons', name)));
    console.log(`Staged ${path.relative(repoRoot, dest)}`);
}
