// Generates ADA's app icons from one source image. Run on macOS from the repository root:
//   swift build/make-icons.swift ADA/Assets/ada-icon.png
// Writes ADA/Assets/ada.ico (Windows, also the window icon), build/icons/ada.icns (macOS) and
// build/icons/ada.png (Linux, 512 px). Commit the results; the release workflow uses them as they are.
import AppKit

let CANVAS: CGFloat = 1024
let TILE: CGFloat = 824
let CORNER_RADIUS: CGFloat = 185
// How much of the tile the source may fill, so the glyph clears the rounded corners.
let SOURCE_SCALE: CGFloat = 0.8
let ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]
// The concept artwork's charcoal background (#32353B), behind the transparent source glyph.
let TILE_COLOR = CGColor(srgbRed: 0x32 / 255.0, green: 0x35 / 255.0, blue: 0x3B / 255.0, alpha: 1)

guard CommandLine.arguments.count == 2,
      let image = NSImage(contentsOfFile: CommandLine.arguments[1]),
      let source = image.cgImage(forProposedRect: nil, context: nil, hints: nil)
else {
    FileHandle.standardError.write("Usage: swift build/make-icons.swift <source png>\n".data(using: .utf8)!)
    exit(1)
}
/// The icon as a PNG at the given size: the source fitted, without stretching, onto a rounded tile with
/// transparent margins. The proportions match macOS app icons so it sits evenly beside other apps on every platform.
func Render(_ size: Int) -> Data {
    let scale = CGFloat(size) / CANVAS
    let context = CGContext(data: nil, width: size, height: size, bitsPerComponent: 8, bytesPerRow: 0,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!,
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    context.interpolationQuality = .high
    let tile = CGRect(x: (CANVAS - TILE) / 2 * scale, y: (CANVAS - TILE) / 2 * scale, width: TILE * scale, height: TILE * scale)
    context.addPath(CGPath(roundedRect: tile, cornerWidth: CORNER_RADIUS * scale, cornerHeight: CORNER_RADIUS * scale, transform: nil))
    context.clip()
    context.setFillColor(TILE_COLOR)
    context.fill(tile)
    let fit = tile.width * SOURCE_SCALE / CGFloat(max(source.width, source.height))
    let width = CGFloat(source.width) * fit
    let height = CGFloat(source.height) * fit
    context.draw(source, in: CGRect(x: tile.midX - width / 2, y: tile.midY - height / 2, width: width, height: height))
    return NSBitmapImageRep(cgImage: context.makeImage()!).representation(using: .png, properties: [:])!
}

/// A Windows .ico with each size stored as an embedded PNG.
func Ico(_ sizes: [Int]) -> Data {
    func Bytes<T: FixedWidthInteger>(_ value: T) -> Data { withUnsafeBytes(of: value.littleEndian) { Data($0) } }
    let images = sizes.map(Render)
    var header = Bytes(UInt16(0)) + Bytes(UInt16(1)) + Bytes(UInt16(sizes.count))
    var offset = 6 + 16 * sizes.count
    for (size, png) in zip(sizes, images) {
        // A width or height of 0 means 256.
        header += Data([UInt8(size % 256), UInt8(size % 256), 0, 0]) + Bytes(UInt16(1)) + Bytes(UInt16(32))
        header += Bytes(UInt32(png.count)) + Bytes(UInt32(offset))
        offset += png.count
    }
    return images.reduce(header, +)
}

try! FileManager.default.createDirectory(atPath: "build/icons", withIntermediateDirectories: true)
try! Ico(ICO_SIZES).write(to: URL(fileURLWithPath: "ADA/Assets/ada.ico"))
try! Render(512).write(to: URL(fileURLWithPath: "build/icons/ada.png"))

let iconset = FileManager.default.temporaryDirectory.appendingPathComponent("ada.iconset")
try? FileManager.default.removeItem(at: iconset)
try! FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)
for points in [16, 32, 128, 256, 512] {
    try! Render(points).write(to: iconset.appendingPathComponent("icon_\(points)x\(points).png"))
    try! Render(points * 2).write(to: iconset.appendingPathComponent("icon_\(points)x\(points)@2x.png"))
}
let iconutil = Process()
iconutil.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
iconutil.arguments = ["--convert", "icns", "--output", "build/icons/ada.icns", iconset.path]
try! iconutil.run()
iconutil.waitUntilExit()
exit(iconutil.terminationStatus)
