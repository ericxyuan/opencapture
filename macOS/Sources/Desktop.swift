import AppKit
import ScreenCaptureKit

/// The screenshot, HEX sampler and crops all share the same top-left, sRGB pixels.
final class Pixels {
    let image: CGImage
    private let bitmap: CGContext
    // The retained context owns this immutable sRGB buffer for the lifetime of the snapshot.
    private let bytes: UnsafePointer<UInt8>
    private let stride: Int
    private let lastColumn: Int
    private let lastRow: Int
    let sx: CGFloat
    let sy: CGFloat

    init(_ source: CGImage, pointSize: CGSize) throws {
        guard let context = CGContext(data: nil, width: source.width, height: source.height,
              bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB)!,
              bitmapInfo: CGBitmapInfo.byteOrder32Big.rawValue | CGImageAlphaInfo.noneSkipLast.rawValue)
        else { throw CaptureError("Insufficient memory to freeze this display.") }
        context.draw(source, in: CGRect(x: 0, y: 0, width: source.width, height: source.height))
        guard let image = context.makeImage(), let data = context.data else { throw CaptureError("Couldn’t freeze this display.") }
        self.image = image
        self.bitmap = context
        self.bytes = UnsafeRawPointer(data).assumingMemoryBound(to: UInt8.self)
        self.stride = context.bytesPerRow
        self.lastColumn = source.width - 1
        self.lastRow = source.height - 1
        self.sx = CGFloat(source.width) / pointSize.width
        self.sy = CGFloat(source.height) / pointSize.height
    }

    func rect(_ points: CGRect) -> CGRect {
        points.applying(CGAffineTransform(scaleX: sx, y: sy)).integral
            .intersection(CGRect(x: 0, y: 0, width: image.width, height: image.height))
    }

    func rgb(_ point: CGPoint) -> (Int, Int, Int) {
        let x = max(0, min(lastColumn, Int(point.x * sx)))
        let y = max(0, min(lastRow, Int(point.y * sy)))
        return withExtendedLifetime(bitmap) {
            let p = bytes + y * stride + x * 4
            return (Int(p[0]), Int(p[1]), Int(p[2]))
        }
    }

    func hex(_ point: CGPoint) -> String {
        let (r, g, b) = rgb(point)
        return String(format: "#%02X%02X%02X", r, g, b)
    }

    /// Inspect bounded flat-colour surfaces within the frontmost window. Check
    /// several interior rows so a single stripe of a photo cannot claim a whole panel.
    func panel(at point: CGPoint, inside bounds: CGRect) -> CGRect? {
        let origin = rgb(point)
        func matches(_ x: CGFloat, _ y: CGFloat) -> Bool {
            let c = rgb(CGPoint(x: x, y: y))
            return max(abs(c.0 - origin.0), abs(c.1 - origin.1), abs(c.2 - origin.2)) <= 10
        }
        func edge(_ dx: CGFloat, _ dy: CGFloat) -> CGPoint {
            var p = point
            while true {
                let next = CGPoint(x: p.x + dx, y: p.y + dy)
                guard bounds.contains(next), matches(next.x, next.y) else { return p }
                p = next
            }
        }
        let x = edge(-1, 0).x, y = edge(0, -1).y
        let box = CGRect(x: x, y: y, width: edge(1, 0).x - x + 1, height: edge(0, 1).y - y + 1)
        guard box.width >= 24, box.height >= 16,
              box.width * box.height < 0.90 * bounds.width * bounds.height else { return nil }
        var agreement = 0
        for fx in [0.12, 0.5, 0.88] {
            for fy in [0.12, 0.5, 0.88] where matches(x + box.width * fx, y + box.height * fy) { agreement += 1 }
        }
        return agreement >= 7 ? box : nil
    }
}

struct FrozenDisplay {
    let screen: NSScreen
    let filter: SCContentFilter
    let pixels: Pixels
    let windows: [CGRect] // front to back, local points, top-left origin

    func zone(at point: CGPoint) -> CGRect? {
        guard let window = windows.first(where: { $0.contains(point) }) else { return nil }
        if let panel = pixels.panel(at: point, inside: window) { return panel }
        let frame = screen.frame
        let usable = screen.visibleFrame
        let workArea = CGRect(x: usable.minX - frame.minX, y: frame.maxY - usable.maxY, width: usable.width, height: usable.height)
        if window.insetBy(dx: -2, dy: -2).contains(workArea) { return nil }
        return window
    }
}

enum Desktop {
    @MainActor static func freeze() async throws -> [FrozenDisplay] {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        let ourApps = content.applications.filter { $0.processID == ProcessInfo.processInfo.processIdentifier }
        let info = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] ?? []
        let windowBounds: [CGRect] = info.compactMap { window in
            guard window[kCGWindowLayer as String] as? Int == 0,
                  (window[kCGWindowAlpha as String] as? Double ?? 1) > 0.01,
                  window[kCGWindowOwnerPID as String] as? Int32 != ProcessInfo.processInfo.processIdentifier,
                  let bounds = window[kCGWindowBounds as String] as? [String: Any]
            else { return nil }
            return CGRect(dictionaryRepresentation: bounds as CFDictionary)
        }
        var frames: [FrozenDisplay] = []
        for screen in NSScreen.screens {
            guard let number = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber,
                  let display = content.displays.first(where: { $0.displayID == number.uint32Value }) else { continue }
            let filter = SCContentFilter(display: display, excludingApplications: ourApps, exceptingWindows: [])
            filter.includeMenuBar = true
            let config = SCStreamConfiguration()
            config.width = Int((screen.frame.width * CGFloat(filter.pointPixelScale)).rounded())
            config.height = Int((screen.frame.height * CGFloat(filter.pointPixelScale)).rounded())
            config.showsCursor = false
            config.colorSpaceName = CGColorSpace.sRGB
            let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
            let pixels = try Pixels(image, pointSize: screen.frame.size)
            let area = CGDisplayBounds(display.displayID)
            let windows = windowBounds.compactMap { rect -> CGRect? in
                let part = area.intersection(rect)
                guard part.width >= 8, part.height >= 8 else { return nil }
                return part.offsetBy(dx: -area.minX, dy: -area.minY)
            }
            frames.append(FrozenDisplay(screen: screen, filter: filter, pixels: pixels, windows: windows))
        }
        guard !frames.isEmpty else { throw CaptureError("No active displays were available.") }
        return frames
    }
}
