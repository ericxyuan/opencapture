import AppKit
import AVFoundation
import ScreenCaptureKit

// Exercise real ScreenCaptureKit and Vision with only this test's own window.
// Keep the user's captures, clipboard and preferences out of the test.
private final class TestCard: NSView {
    var title = "OpenCapture Test"
    override var isFlipped: Bool { true }
    override func draw(_ dirtyRect: CGRect) {
        NSColor.white.setFill(); bounds.fill()
        NSColor(srgbRed: 1, green: 0, blue: 0, alpha: 1).setFill()
        CGRect(x: 8, y: 8, width: 75, height: 32).fill()
        (title as NSString).draw(at: CGPoint(x: 36, y: 67), withAttributes: [.font: NSFont.systemFont(ofSize: 34, weight: .semibold), .foregroundColor: NSColor.black])
    }
}

@main struct Smoke {
    @MainActor static func main() async throws {
        let app = NSApplication.shared
        app.setActivationPolicy(.accessory)
        let screen = NSScreen.main!
        let rect = CGRect(x: screen.visibleFrame.midX - 210, y: screen.visibleFrame.midY - 70, width: 420, height: 140)
        let window = NSWindow(contentRect: rect, styleMask: .borderless, backing: .buffered, defer: false)
        window.backgroundColor = .white; window.isReleasedWhenClosed = false; window.hasShadow = false
        let card = TestCard(frame: CGRect(origin: .zero, size: rect.size))
        window.contentView = card
        window.orderFrontRegardless(); window.displayIfNeeded()
        defer { window.orderOut(nil) }
        try await Task.sleep(for: .milliseconds(250))
        let content = try await SCShareableContent.currentProcess
        guard let own = content.windows.first(where: { $0.windowID == CGWindowID(window.windowNumber) }) else {
            throw CaptureError("Couldn’t find the test window.")
        }
        let filter = SCContentFilter(desktopIndependentWindow: own)
        let config = SCStreamConfiguration()
        config.width = 840; config.height = 280
        config.scalesToFit = true; config.captureResolution = .best
        config.destinationRect = CGRect(x: 0, y: 0, width: 840, height: 280)
        config.ignoreShadowsSingleWindow = true; config.showsCursor = false
        config.colorSpaceName = CGColorSpace.sRGB
        var rendered: Pixels?
        var observed = ""
        for _ in 0..<20 {
            let snapshot = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
            let candidate = try Pixels(snapshot, pointSize: rect.size)
            observed = candidate.hex(CGPoint(x: 20, y: 20))
            if observed == "#FF0000" { rendered = candidate; break }
            try await Task.sleep(for: .milliseconds(100))
        }
        guard let px = rendered else { throw CaptureError("ScreenCaptureKit test patch was \(observed), expected #FF0000.") }
        precondition(px.hex(CGPoint(x: 20, y: 20)) == "#FF0000", "Top-left HEX sampling")
        precondition(px.hex(CGPoint(x: 20, y: 110)) == "#FFFFFF", "Vertical orientation")
        let zone = px.panel(at: CGPoint(x: 20, y: 20), inside: CGRect(origin: .zero, size: rect.size))!
        // Resampling a 1× external display softens the outermost pixels.
        precondition(abs(zone.minX - 8) <= 3 && abs(zone.height - 32) <= 4, "Bounded colour zone")
        let crop = px.image.cropping(to: px.rect(CGRect(x: 0, y: 58, width: 420, height: 82)))!
        precondition(crop.width == 840 && crop.height == 164, "Retina crop")
        let read = try await Task.detached { try OCR.read(crop) }.value
        precondition(read.text.localizedCaseInsensitiveContains("OpenCapture Test") && !read.boxes.isEmpty, "Vision OCR: \(read.text)")

        let id = UUID().uuidString
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(id)
        let defaults = UserDefaults(suiteName: id)!
        let store = MediaStore(root: root, defaults: defaults)
        defer { try? FileManager.default.removeItem(at: root); defaults.removePersistentDomain(forName: id) }
        let png = try MediaStore.png(crop)
        let first = try store.savePNG(png)
        let second = try store.savePNG(png)
        precondition(first != second && store.managed(.images).count == 2)
        let reopened = CGImageSourceCreateWithURL(first as CFURL, nil)!
        precondition(CGImageSourceCreateImageAtIndex(reopened, 0, nil)!.width == 840)
        let altered = first.deletingLastPathComponent().appendingPathComponent("renamed.png")
        try FileManager.default.moveItem(at: first, to: altered)
        try FileManager.default.moveItem(at: altered, to: first)
        precondition(store.managed(.images) == [second], "Renamed-and-restored file must be protected")
        let a = try store.savePNG(png, text: "../a:b\ntext")
        let b = try store.savePNG(png, text: "../a:b\ntext")
        precondition(a != b && a.deletingLastPathComponent().lastPathComponent == "Copied Text")
        let board = NSPasteboard.withUniqueName()
        defer { board.releaseGlobally() }
        CaptureClipboard.copy(png, type: .png, to: board)
        precondition(board.data(forType: .png) == png && board.types!.contains(CaptureClipboard.marker))
        print("PASS  Own-window screenshot, Retina crop, HEX, hover zone, OCR, PNG, clipboard and storage")

        let frozen = FrozenDisplay(screen: screen, filter: filter, pixels: px, windows: [CGRect(origin: .zero, size: rect.size)])
        let panel = OverlayPanel(contentRect: rect, styleMask: .borderless, backing: .buffered, defer: false)
        var result: SelectionResult?
        let selection = SelectionView(desktop: frozen, mode: .image, bindings: Bindings()) { result = $0 }
        panel.contentView = selection
        panel.setContentSize(rect.size)
        selection.frame = CGRect(origin: .zero, size: rect.size)
        func mouse(_ type: NSEvent.EventType, x: CGFloat, y: CGFloat) -> NSEvent {
            NSEvent.mouseEvent(with: type, location: CGPoint(x: x, y: rect.height - y), modifierFlags: [],
                               timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: panel.windowNumber,
                               context: nil, eventNumber: 1, clickCount: 1, pressure: 1)!
        }
        selection.mouseDown(with: mouse(.leftMouseDown, x: 8, y: 8))
        selection.mouseDragged(with: mouse(.leftMouseDragged, x: 83, y: 40))
        selection.mouseUp(with: mouse(.leftMouseUp, x: 83, y: 40))
        guard case .image(let selected) = result else { throw CaptureError("Drag did not select an image.") }
        precondition(selected.width == 150 && selected.height == 64)
        let selectedPixels = try Pixels(selected, pointSize: CGSize(width: 75, height: 32))
        precondition(selectedPixels.hex(CGPoint(x: 20, y: 20)) == "#FF0000")
        print("PASS  Overlay drag selected the expected Retina pixels")
        var outcome: Result<URL, Error>?
        let recorder = try Recorder(store: store) { outcome = $0 }
        await recorder.start(frozen, area: CGRect(origin: .zero, size: rect.size))
        try await Task.sleep(for: .milliseconds(700))
        card.title = "Recording frame two"; card.needsDisplay = true; window.displayIfNeeded()
        try await Task.sleep(for: .milliseconds(400))
        recorder.stop()
        for _ in 0..<100 where outcome == nil { try await Task.sleep(for: .milliseconds(100)) }
        guard let outcome else { throw CaptureError("Recording failed to finalize in time.") }
        let movie = try outcome.get()
        let asset = AVURLAsset(url: movie)
        let duration = try await asset.load(.duration).seconds
        let tracks = try await asset.loadTracks(withMediaType: .video)
        guard let track = tracks.first else { throw CaptureError("MP4 contained no video.") }
        let size = try await track.load(.naturalSize)
        precondition(duration >= 0.5 && size.width == 840 && size.height == 280, "MP4 dimensions or duration")
        precondition(store.managed(.videos) == [movie])
        CaptureClipboard.copy(movie.dataRepresentation, type: .fileURL, to: board)
        precondition((board.readObjects(forClasses: [NSURL.self])?.first as? URL) == movie)
        print("PASS  Real ScreenCaptureKit MP4 start/stop, \(Int(size.width)) × \(Int(size.height)), \(duration)s, file clipboard")
    }
}
