#!/usr/bin/env swift
//
// Renders every piece of macOS artwork the app needs:
//
//   src/MonitorScreenSaver.Mac/Assets/MenuBarIcon.png (+@2x)
//       The status item glyph — a monitor on a stand with "SS" on its screen. Drawn from
//       scratch rather than resampled from the app icon, because a status item image is a
//       *template*: AppKit throws the colours away, keeps the alpha channel and tints the
//       result for the current menu bar. An illustration reduced to 18 pt loses its shape,
//       and its alpha becomes a grey smudge. Flat art at the target size is the only kind
//       that reads there.
//
//   <iconset>/icon_*.png    (with --iconset; tools/make-icns.sh turns these into the .icns)
//       The app icon: the artwork on a near-black rounded tile. The artwork alone has a
//       transparent background, which on the Dock reads as a sticker floating on whatever
//       is behind it — every other Dock tile is an opaque rounded square, so this one is
//       too. Each size is rendered on its own instead of resampling one master, so the
//       tile's corners stay clean at 16 pt.
//
// Needs Xcode Command Line Tools for /usr/bin/swift. The outputs are committed, exactly
// like MonitorScreenSaver.ico, so nobody has to run this to build the app.
//
//   tools/make-mac-icons.swift [--iconset <dir>] [--preview]
import AppKit

let root = URL(fileURLWithPath: CommandLine.arguments[0])
    .deletingLastPathComponent().deletingLastPathComponent().path
let assets = "\(root)/src/MonitorScreenSaver.Mac/Assets"

// ---------------------------------------------------------------- menu bar glyph

// Canvas, in points. Wider than tall because the artwork is: 18 pt is the menu bar's
// usable height, and a status item may be wider than that. Everything below is sized off
// the screen opening, which is the binding constraint — "SS" has to fit inside the bezel
// at 8 pt tall for the 1x rep (a non-Retina external display) to be readable at all, and
// 8 pt of opening plus bezel plus stand is what sets the rest.
let barWidth: CGFloat = 20
let barHeight: CGFloat = 18
let stroke: CGFloat = 1.5

let screenBody = NSRect(x: 1, y: 4.6, width: 18, height: 12.2)

/// Monitor outline, drawn y-up. Stroked rather than filled so the screen stays open for
/// the letters; the stand is filled so it survives at 1x.
func drawMenuBarGlyph() {
    NSColor.black.setStroke()
    NSColor.black.setFill()

    let bezel = NSBezierPath(roundedRect: screenBody, xRadius: 2.4, yRadius: 2.4)
    bezel.lineWidth = stroke
    bezel.stroke()

    NSBezierPath(rect: NSRect(x: screenBody.midX - 1.7, y: 3.2, width: 3.4, height: 1.6)).fill()
    NSBezierPath(roundedRect: NSRect(x: screenBody.midX - 5.5, y: 1.6, width: 11, height: 1.6),
                 xRadius: 0.8, yRadius: 0.8).fill()

    // "SS", scaled to fit the screen area rather than trusted to a point size: the system
    // font's advance widths change between macOS releases, and a glyph that overflows the
    // bezel by half a point is obvious at this size. The inset is half the stroke (to
    // clear the bezel itself) plus a gap, so the letters read as being *on* the screen
    // rather than jammed against its edge.
    let screen = screenBody.insetBy(dx: stroke / 2 + 1.1, dy: stroke / 2 + 1.1)

    // Glyph outlines rather than NSAttributedString.draw(at:): drawn text is positioned by
    // its line box (ascent, descent and leading included), so fitting it to a 5 pt opening
    // would scale the letters down to the size of the invisible padding around them. A
    // path's bounding box is the ink itself, which is what has to fit.
    let letters = outline("SS", font: .systemFont(ofSize: 8, weight: .black), kern: -0.6)
    let ink = letters.boundingBoxOfPath
    let scale = min(screen.width / ink.width, screen.height / ink.height)

    var fit = CGAffineTransform(translationX: screen.midX, y: screen.midY)
        .scaledBy(x: scale, y: scale)
        .translatedBy(x: -ink.midX, y: -ink.midY)

    if let placed = letters.copy(using: &fit), let ctx = NSGraphicsContext.current?.cgContext {
        ctx.addPath(placed)
        ctx.fillPath()
    }
}

/// The ink of a string as a single path, in the font's own coordinate space.
func outline(_ string: String, font: NSFont, kern: CGFloat) -> CGPath {
    let line = CTLineCreateWithAttributedString(NSAttributedString(
        string: string, attributes: [.font: font, .kern: kern]))

    let path = CGMutablePath()
    let runs = CTLineGetGlyphRuns(line)

    for i in 0..<CFArrayGetCount(runs) {
        let run = unsafeBitCast(CFArrayGetValueAtIndex(runs, i), to: CTRun.self)
        let runFont = (CTRunGetAttributes(run) as NSDictionary)[kCTFontAttributeName] as! CTFont
        let count = CTRunGetGlyphCount(run)

        var glyphs = [CGGlyph](repeating: 0, count: count)
        var positions = [CGPoint](repeating: .zero, count: count)
        CTRunGetGlyphs(run, CFRangeMake(0, count), &glyphs)
        CTRunGetPositions(run, CFRangeMake(0, count), &positions)

        for g in 0..<count {
            guard let glyph = CTFontCreatePathForGlyph(runFont, glyphs[g], nil) else { continue }
            path.addPath(glyph, transform: CGAffineTransform(
                translationX: positions[g].x, y: positions[g].y))
        }
    }

    return path
}

// ---------------------------------------------------------------- app icon

// Proportions from Apple's macOS icon grid: the tile covers 824 of a 1024 pt canvas, with
// ~185 pt corners. The margin is not wasted space — the Dock scales tiles to a fixed box,
// so an icon drawn edge to edge looks bigger than everything beside it.
let tileMargin: CGFloat = 100.0 / 1024
let tileCorner: CGFloat = 185.0 / 1024
let artFraction: CGFloat = 0.70

let iconArt = "\(root)/src/MonitorScreenSaver.Windows/Assets/icon.png"

func drawAppIcon(_ size: CGFloat, _ art: NSImage) {
    let tile = NSRect(x: 0, y: 0, width: size, height: size)
        .insetBy(dx: size * tileMargin, dy: size * tileMargin)
    let radius = size * tileCorner

    // Near-black, with a gradient shallow enough to read as flat: it stops the tile
    // looking like a hole punched in the Dock without turning into a colour of its own.
    // #0E0F14 is the settings window's own background (UI/Theme.axaml "Bg").
    let path = NSBezierPath(roundedRect: tile, xRadius: radius, yRadius: radius)
    let ground = NSGradient(
        starting: NSColor(srgbRed: 0x08 / 255.0, green: 0x09 / 255.0, blue: 0x0D / 255.0, alpha: 1),
        ending: NSColor(srgbRed: 0x16 / 255.0, green: 0x18 / 255.0, blue: 0x22 / 255.0, alpha: 1))!
    ground.draw(in: path, angle: 90)

    let side = size * artFraction
    art.draw(in: NSRect(x: tile.midX - side / 2, y: tile.midY - side / 2, width: side, height: side),
             from: .zero, operation: .sourceOver, fraction: 1)
}

// ---------------------------------------------------------------- output

/// Renders `body` into a PNG. Drawing happens in points; `scale` sets the pixel density.
func render(_ pointWidth: CGFloat, _ pointHeight: CGFloat, scale: CGFloat,
            to path: String, _ body: () -> Void) {
    let px = Int(pointWidth * scale), py = Int(pointHeight * scale)
    guard let rep = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: py,
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
        colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)
    else { die("cannot allocate a \(px)x\(py) bitmap") }

    rep.size = NSSize(width: px, height: py)

    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    NSGraphicsContext.current?.imageInterpolation = .high
    let up = NSAffineTransform()
    up.scale(by: scale)
    up.concat()
    body()
    NSGraphicsContext.restoreGraphicsState()

    guard let png = rep.representation(using: .png, properties: [:]) else { die("PNG encode failed") }
    do { try png.write(to: URL(fileURLWithPath: path)) } catch { die("\(error)") }
    print("  \(path)  (\(px)x\(py))")
}

func die(_ message: String) -> Never {
    FileHandle.standardError.write("make-mac-icons: \(message)\n".data(using: .utf8)!)
    exit(1)
}

func flag(_ name: String) -> String? {
    guard let i = CommandLine.arguments.firstIndex(of: name),
          i + 1 < CommandLine.arguments.count else { return nil }
    return CommandLine.arguments[i + 1]
}

print("Wrote:")

render(barWidth, barHeight, scale: 1, to: "\(assets)/MenuBarIcon.png", drawMenuBarGlyph)
render(barWidth, barHeight, scale: 2, to: "\(assets)/MenuBarIcon@2x.png", drawMenuBarGlyph)

if let iconset = flag("--iconset") {
    guard let art = NSImage(contentsOfFile: iconArt) else { die("source artwork not found: \(iconArt)") }
    try? FileManager.default.createDirectory(atPath: iconset, withIntermediateDirectories: true)

    // iconutil accepts a partial set. This stops at 256 because the source artwork is
    // 256x256: every size here downsamples it, and a 512 entry would be an upscale.
    // Filling that in needs bigger artwork, not a bigger script.
    for (name, size) in [("icon_16x16", 16.0), ("icon_16x16@2x", 32.0),
                         ("icon_32x32", 32.0), ("icon_32x32@2x", 64.0),
                         ("icon_128x128", 128.0), ("icon_128x128@2x", 256.0),
                         ("icon_256x256", 256.0)] {
        render(size, size, scale: 1, to: "\(iconset)/\(name).png") { drawAppIcon(size, art) }
    }
}

// Blown-up copies for eyeballing the shapes; not shipped.
if CommandLine.arguments.contains("--preview") {
    render(barWidth, barHeight, scale: 10, to: NSTemporaryDirectory() + "MenuBarIcon-preview.png",
           drawMenuBarGlyph)

    if let art = NSImage(contentsOfFile: iconArt) {
        render(512, 512, scale: 1, to: NSTemporaryDirectory() + "AppIcon-preview.png") {
            drawAppIcon(512, art)
        }
    }
}
