// Renders the styled background for the drag-install DMG (see publish-mac.sh / tools/gen-dmg-background.sh).
//
//   swift tools/gen-dmg-background.swift <out.png>
//
// The image is the Finder window's content area (640x400 points) rendered at 2x (1280x800 px) so it stays
// crisp on Retina — the wrapper stamps 144 DPI so Finder treats the pixels as points. It draws a light
// backdrop (Finder paints the uneditable icon labels near-black, so a light ground keeps them legible),
// white "slot" cards, an arrow pointing from where the app icon sits (left) toward the Applications alias
// (right), and a caption. The icon *positions* themselves are set by AppleScript in publish-mac.sh; this
// only paints the backdrop those icons land on, so the geometry here must match its `set position` calls.
import AppKit

// --- geometry (top-left origin, matching Finder's coordinate space; flipped to CG's bottom-left below) ---
let W = 640.0, H = 400.0
let scale = 2.0
let appCenter = CGPoint(x: 172, y: 170)   // must match publish-mac.sh's "Perch.app" position
let appsCenter = CGPoint(x: 468, y: 170)  // ...and its "Applications" position
let iconRadius = 64.0                       // 128px icon

func flip(_ y: Double) -> Double { H - y }  // Finder top-left y -> CG bottom-left y

// --- palette --------------------------------------------------------------------------------------------
// Deliberately LIGHT: Finder paints the icon *filename* labels ("Perch" / "Applications") in near-black and
// AppleScript can't recolor them, so a dark backdrop swallows the labels. A light backdrop keeps them
// legible; Perch identity comes from the warm coral/orange arrow instead of the background colour.
func rgb(_ r: Int, _ g: Int, _ b: Int, _ a: Double = 1) -> NSColor {
    NSColor(srgbRed: CGFloat(r) / 255, green: CGFloat(g) / 255, blue: CGFloat(b) / 255, alpha: CGFloat(a))
}
let bgTop = rgb(0xf7, 0xf7, 0xfb)
let bgBottom = rgb(0xe7, 0xe7, 0xf1)
let arrowStart = rgb(0xf4, 0xa2, 0x61)   // svg orange
let arrowEnd = rgb(0xff, 0x6b, 0x6b)     // svg coral
let captionColor = rgb(0x33, 0x33, 0x42)
let hintColor = rgb(0x7c, 0x7c, 0x8c)

let outPath = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "dmg-background.png"

guard let rep = NSBitmapImageRep(
    bitmapDataPlanes: nil, pixelsWide: Int(W * scale), pixelsHigh: Int(H * scale),
    bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
    colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)
else { fatalError("could not allocate bitmap") }
rep.size = NSSize(width: W, height: H)   // point size vs pixel size => 2x / 144 DPI

NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
let ctx = NSGraphicsContext.current!.cgContext
// rep.size (640x400 pt) already maps onto the 1280x800 px backing, so draw directly in point space —
// an extra scaleBy(scale) here would double-transform everything.

// --- background gradient ---------------------------------------------------------------------------------
NSGradient(starting: bgBottom, ending: bgTop)!.draw(in: NSRect(x: 0, y: 0, width: W, height: H), angle: 90)

// --- soft white cards under each icon slot -------------------------------------------------------------
// White rounded cards with a gentle shadow give the 128px icons a "slot" to land in and lift them off the
// light gradient. The card spans a little below the icon so the black Finder label reads on white too.
func iconPlate(_ c: CGPoint) {
    let halfW = iconRadius + 20
    let top = flip(c.y) + iconRadius + 18        // above the icon
    let bottom = flip(c.y) - iconRadius - 34      // below the icon, covering the label row
    let rect = NSRect(x: c.x - halfW, y: bottom, width: halfW * 2, height: top - bottom)
    let path = NSBezierPath(roundedRect: rect, xRadius: 22, yRadius: 22)
    ctx.saveGState()
    let shadow = NSShadow()
    shadow.shadowColor = rgb(0x1a, 0x1a, 0x2e, 0.18)
    shadow.shadowOffset = NSSize(width: 0, height: -3)
    shadow.shadowBlurRadius = 12
    shadow.set()
    rgb(0xff, 0xff, 0xff, 0.92).setFill()
    path.fill()
    ctx.restoreGState()
    rgb(0x1a, 0x1a, 0x2e, 0.06).setStroke()
    path.lineWidth = 1
    path.stroke()
}
iconPlate(appCenter)
iconPlate(appsCenter)

// --- arrow from the app icon toward the Applications alias -----------------------------------------------
let ay = flip(appCenter.y)
let x1 = appCenter.x + iconRadius + 26   // tail: just past the app icon plate
let x2 = appsCenter.x - iconRadius - 26  // tip:  just before the Applications plate
let shaftH = 13.0, headW = 30.0, headH = 34.0
let arrow = NSBezierPath()
arrow.move(to: NSPoint(x: x1, y: ay - shaftH / 2))
arrow.line(to: NSPoint(x: x2 - headW, y: ay - shaftH / 2))
arrow.line(to: NSPoint(x: x2 - headW, y: ay - headH / 2))
arrow.line(to: NSPoint(x: x2, y: ay))
arrow.line(to: NSPoint(x: x2 - headW, y: ay + headH / 2))
arrow.line(to: NSPoint(x: x2 - headW, y: ay + shaftH / 2))
arrow.line(to: NSPoint(x: x1, y: ay + shaftH / 2))
arrow.close()
ctx.saveGState()
arrow.addClip()
NSGradient(starting: arrowStart, ending: arrowEnd)!.draw(in: arrow.bounds, angle: 0)
ctx.restoreGState()

// --- text ------------------------------------------------------------------------------------------------
func centered(_ s: String, font: NSFont, color: NSColor, topY: Double) {
    let para = NSMutableParagraphStyle()
    para.alignment = .center
    let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: color, .paragraphStyle: para]
    let size = (s as NSString).size(withAttributes: attrs)
    let rect = NSRect(x: 0, y: flip(topY) - size.height, width: W, height: size.height)
    (s as NSString).draw(in: rect, withAttributes: attrs)
}
// Caption sits below the icon labels (Finder draws filenames right under the 128px icons ~y+150).
centered("Drag Perch onto the Applications folder",
         font: .systemFont(ofSize: 18, weight: .semibold), color: captionColor, topY: 300)
centered("Then eject this disk and launch Perch from Applications.",
         font: .systemFont(ofSize: 12, weight: .regular), color: hintColor, topY: 330)

NSGraphicsContext.current!.flushGraphics()
NSGraphicsContext.restoreGraphicsState()

guard let png = rep.representation(using: .png, properties: [:]) else { fatalError("PNG encode failed") }
try! png.write(to: URL(fileURLWithPath: outPath))
FileHandle.standardError.write("Wrote \(outPath) (\(Int(W * scale))x\(Int(H * scale)))\n".data(using: .utf8)!)
