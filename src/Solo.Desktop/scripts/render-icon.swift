// Renders flat PNGs out of a compiled Icon Composer asset catalog.
//
// macOS itself renders the Liquid Glass icon live from the catalog, so the
// catalog holds a per-appearance layer recipe rather than flat bitmaps — there
// is nothing to simply extract. The two appearances therefore come from two
// different lookups:
//
//   light — Bundle.image(forResource:) returns the catalog's base rendition.
//           Deterministic, unaffected by the machine's appearance setting.
//   dark  — only IconServices (NSWorkspace.icon) composites the dark rendition,
//           and it bakes in whatever the *system* appearance is at the time.
//           Appearance overrides don't reach it: it renders out of process.
//
// So a dark render requires the machine to be in Dark Mode. build-icon.mjs
// checks the result and fails loudly rather than silently shipping two copies
// of the light icon.
//
// Framing modes:
//   fit  — the macOS canvas as-is: the rounded shape sits on the 824/1024 grid
//          with its drop shadow in the surrounding margin.
//   fill — the rounded shape alone, edge to edge, shadow cropped away. For the
//          web icons: browsers don't add a shadow, and iOS masks a home-screen
//          icon with its own squircle, so a margin there just insets the mark.
//          Bounds are measured off a render rather than assumed, so this holds
//          up if the artwork's grid ever changes.
//
// usage: render-icon <bundle.app> <light|dark> <fit|fill> <size>:<out.png> ...
import AppKit

let args = Array(CommandLine.arguments.dropFirst())
guard args.count >= 4 else {
    FileHandle.standardError.write("usage: render-icon <bundle.app> <light|dark> <fit|fill> <size>:<out.png> ...\n".data(using: .utf8)!)
    exit(2)
}

let bundlePath = args[0]
let appearanceMode = args[1]
let framingMode = args[2]
guard framingMode == "fit" || framingMode == "fill" else {
    FileHandle.standardError.write("framing must be 'fit' or 'fill'\n".data(using: .utf8)!)
    exit(2)
}

let outputs: [(size: Int, path: String)] = args.dropFirst(3).map { spec in
    let parts = spec.split(separator: ":", maxSplits: 1)
    guard parts.count == 2, let size = Int(parts[0]) else {
        FileHandle.standardError.write("bad output spec '\(spec)', want <size>:<path>\n".data(using: .utf8)!)
        exit(2)
    }
    return (size, String(parts[1]))
}

let icon: NSImage
switch appearanceMode {
case "light":
    guard let bundle = Bundle(path: bundlePath), let image = bundle.image(forResource: "Icon") else {
        FileHandle.standardError.write("no 'Icon' in the asset catalog at \(bundlePath)\n".data(using: .utf8)!)
        exit(3)
    }
    icon = image
case "dark":
    icon = NSWorkspace.shared.icon(forFile: bundlePath)
default:
    FileHandle.standardError.write("appearance must be 'light' or 'dark'\n".data(using: .utf8)!)
    exit(2)
}

func bitmap(_ size: Int) -> NSBitmapImageRep {
    guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil,
                                     pixelsWide: size, pixelsHigh: size,
                                     bitsPerSample: 8, samplesPerPixel: 4,
                                     hasAlpha: true, isPlanar: false,
                                     colorSpaceName: .deviceRGB,
                                     bytesPerRow: 0, bitsPerPixel: 0) else {
        FileHandle.standardError.write("could not allocate a \(size)px bitmap\n".data(using: .utf8)!)
        exit(1)
    }
    rep.size = NSSize(width: size, height: size)
    return rep
}

/// Draws the icon into a `size`x`size` bitmap, with the icon's own canvas
/// placed at `canvas` (in that bitmap's bottom-left-origin coordinates).
func draw(into rep: NSBitmapImageRep, canvas: NSRect) {
    NSGraphicsContext.saveGraphicsState()
    let context = NSGraphicsContext(bitmapImageRep: rep)
    context?.imageInterpolation = .high
    NSGraphicsContext.current = context
    icon.draw(in: canvas, from: .zero, operation: .sourceOver, fraction: 1.0)
    NSGraphicsContext.restoreGraphicsState()
}

/// The opaque shape's bounds within the icon canvas, as fractions of it.
/// Thresholded well above zero so the soft drop shadow doesn't count.
func shapeBounds() -> NSRect {
    let probe = 512
    let rep = bitmap(probe)
    draw(into: rep, canvas: NSRect(x: 0, y: 0, width: probe, height: probe))

    var minX = probe, minY = probe, maxX = -1, maxY = -1
    for y in 0..<probe {
        for x in 0..<probe {
            guard let alpha = rep.colorAt(x: x, y: y)?.alphaComponent, alpha >= 0.5 else { continue }
            if x < minX { minX = x }
            if x > maxX { maxX = x }
            if y < minY { minY = y }
            if y > maxY { maxY = y }
        }
    }
    guard maxX >= minX, maxY >= minY else {
        FileHandle.standardError.write("the render is empty — nothing opaque to measure\n".data(using: .utf8)!)
        exit(1)
    }

    // colorAt is top-left origin; convert to the bottom-left origin used for drawing.
    let side = CGFloat(probe)
    return NSRect(x: CGFloat(minX) / side,
                  y: CGFloat(probe - 1 - maxY) / side,
                  width: CGFloat(maxX - minX + 1) / side,
                  height: CGFloat(maxY - minY + 1) / side)
}

let shape = framingMode == "fill" ? shapeBounds() : NSRect(x: 0, y: 0, width: 1, height: 1)

for (size, path) in outputs {
    let rep = bitmap(size)
    // Scale the canvas up so the shape spans the output, then shift the shape's
    // corner to the origin. In fit mode this is the identity.
    let canvas = NSRect(x: -shape.minX / shape.width * CGFloat(size),
                        y: -shape.minY / shape.height * CGFloat(size),
                        width: CGFloat(size) / shape.width,
                        height: CGFloat(size) / shape.height)
    draw(into: rep, canvas: canvas)

    guard let png = rep.representation(using: .png, properties: [:]) else {
        FileHandle.standardError.write("could not encode \(path)\n".data(using: .utf8)!)
        exit(1)
    }
    try png.write(to: URL(fileURLWithPath: path))
}
