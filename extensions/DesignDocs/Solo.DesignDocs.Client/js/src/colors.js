// A member's color from their slot: golden-angle hue steps, so everyone keeps
// their color when someone joins, in OKLCH, which spaces hues more evenly to
// the eye than HSL. Converted to rgb() here, so Safari before 15.4 copes too.

const GOLDEN_ANGLE = 137.508;

export function hueOf(slot) {
    return ((slot * GOLDEN_ANGLE) % 360 + 360) % 360;
}

/** The member's color as CSS rgb(), for a light or dark scheme. */
export function memberColor(slot, dark = false) {
    return oklchToRgb(dark ? 0.78 : 0.58, 0.14, hueOf(slot));
}

/** The same hue, faint, for a selection highlight. */
export function memberHighlight(slot, dark = false) {
    return oklchToRgb(dark ? 0.42 : 0.9, 0.07, hueOf(slot), 0.5);
}

export function oklchToRgb(l, c, h, alpha = 1) {
    const radians = (h * Math.PI) / 180;
    const a = c * Math.cos(radians);
    const b = c * Math.sin(radians);

    // OKLab to linear sRGB (Björn Ottosson, https://bottosson.github.io/posts/oklab/).
    const l_ = (l + 0.3963377774 * a + 0.2158037573 * b) ** 3;
    const m_ = (l - 0.1055613458 * a - 0.0638541728 * b) ** 3;
    const s_ = (l - 0.0894841775 * a - 1.291485548 * b) ** 3;
    const linear = [
        4.0767416621 * l_ - 3.3077115913 * m_ + 0.2309699292 * s_,
        -1.2684380046 * l_ + 2.6097574011 * m_ - 0.3413193965 * s_,
        -0.0041960863 * l_ - 0.7034186147 * m_ + 1.707614701 * s_,
    ];
    const [r, g, bl] = linear.map(v => {
        const clamped = Math.min(1, Math.max(0, v));
        const encoded = clamped <= 0.0031308 ? 12.92 * clamped : 1.055 * clamped ** (1 / 2.4) - 0.055;
        return Math.round(encoded * 255);
    });
    return alpha === 1 ? `rgb(${r}, ${g}, ${bl})` : `rgba(${r}, ${g}, ${bl}, ${alpha})`;
}
