import AppKit
import ScreenCaptureKit

@main struct PinyinSmoke {
    @MainActor static func main() async throws {
        _ = NSApplication.shared
        let mixed = "A1重庆銀行、音樂！🙂女绿"
        let readings = Pinyin.readings(in: mixed)
        precondition(mixed.indices.compactMap { readings[$0] } == ["chóng", "qìng", "yín", "háng", "yīn", "yuè", "nǚ", "lǜ"])
        precondition(Pinyin.readings(in: "123, hello! 👋").isEmpty)

        let size = CGSize(width: 700, height: 280)
        let bounds = CGRect(origin: .zero, size: size)
        let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 1400, pixelsHigh: 560,
                                      bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                      colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
        NSGraphicsContext.saveGraphicsState()
        let context = NSGraphicsContext(bitmapImageRep: bitmap)!
        NSGraphicsContext.current = context
        context.cgContext.scaleBy(x: 2, y: 2)
        NSColor.white.setFill(); bounds.fill()
        var expectedX: [CGFloat] = []
        for (y, size) in [(180, 24), (55, 48)] {
            let font = NSFont(name: "PingFangSC-Regular", size: CGFloat(size))!
            let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: NSColor.black]
            ("123 中文" as NSString).draw(at: CGPoint(x: 72, y: y), withAttributes: attrs)
            let first = 72 + ("123 " as NSString).size(withAttributes: attrs).width
            expectedX += [first + CGFloat(size) * 0.5, first + CGFloat(size) * 1.5]
        }
        NSGraphicsContext.restoreGraphicsState()

        let image = bitmap.cgImage!
        let px = try Pixels(image, pointSize: size)
        // Leave enough room to the right for the cursor HUD, outside the measured text.
        let area = CGRect(x: 35, y: 35, width: 320, height: 213)
        let crop = image.cropping(to: px.rect(area))!
        let result = try await Task.detached { try OCR.read(crop, includePinyin: true) }.value
        let labels = result.pinyin.compactMap { PinyinLabel($0, crop: area, screen: bounds) }
            .sorted { a, b in abs(a.glyph.midY - b.glyph.midY) > 15 ? a.glyph.midY < b.glyph.midY : a.glyph.midX < b.glyph.midX }
        precondition(labels.map(\.text) == ["zhōng", "wén", "zhōng", "wén"], "OCR pinyin: \(result.text) \(labels.map(\.text))")
        precondition(result.text.contains("中文") && !result.text.contains("zhōng"))
        for (i, label) in labels.enumerated() {
            precondition(abs(label.glyph.midX - expectedX[i]) < label.glyph.width / 2)
            precondition(label.frame.maxY <= label.glyph.minY && bounds.contains(label.frame))
        }
        precondition(labels[2].font.pointSize > 1.6 * labels[0].font.pointSize)
        precondition(labels[0].frame.maxX <= labels[1].frame.minX + 0.1)
        let edge = PinyinLabel(PinyinAnnotation(syllable: "zhōng", box: CGRect(x: 0, y: 0.9, width: 0.04, height: 0.1)),
                               crop: bounds, screen: bounds)!
        precondition(edge.frame.minY >= edge.glyph.maxY && bounds.contains(edge.frame))

        // Hold a real SelectionView drag open while its asynchronous OCR completes.
        let screen = NSScreen.main!
        let content = try await SCShareableContent.currentProcess
        let filter = SCContentFilter(display: content.displays[0], excludingWindows: [])
        let desktop = FrozenDisplay(screen: screen, filter: filter, pixels: px, windows: [])
        var output: SelectionResult?
        let view = SelectionView(desktop: desktop, mode: .image, bindings: Bindings()) { output = $0 }
        let panel = OverlayPanel(contentRect: bounds, styleMask: .borderless, backing: .buffered, defer: false)
        panel.contentView = view; panel.setContentSize(size); view.frame = bounds
        panel.orderBack(nil)
        defer { panel.orderOut(nil) }
        func mouse(_ kind: NSEvent.EventType, _ point: CGPoint) -> NSEvent {
            NSEvent.mouseEvent(with: kind, location: CGPoint(x: point.x, y: size.height - point.y),
                               modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
                               windowNumber: panel.windowNumber, context: nil, eventNumber: 1, clickCount: 1, pressure: 1)!
        }
        let end = CGPoint(x: area.maxX, y: area.maxY)
        view.mouseDown(with: mouse(.leftMouseDown, area.origin))
        view.mouseDragged(with: mouse(.leftMouseDragged, end))
        func showsLabel(_ rect: CGRect) -> Bool {
            let region = rect.integral
            let rep = view.bitmapImageRepForCachingDisplay(in: region)!
            view.cacheDisplay(in: region, to: rep)
            var dark = 0
            for y in 0..<rep.pixelsHigh {
                for x in 0..<rep.pixelsWide {
                    if let c = rep.colorAt(x: x, y: y)?.usingColorSpace(.sRGB), c.redComponent < 0.35 { dark += 1 }
                }
            }
            return dark > rep.pixelsWide * rep.pixelsHigh / 5
        }
        for _ in 0..<100 {
            if labels.allSatisfy({ showsLabel($0.frame) }) { break }
            try await Task.sleep(for: .milliseconds(100))
        }
        precondition(labels.allSatisfy { showsLabel($0.frame) }, "Live selection did not paint both lines of pinyin")
        let upperLineOnly = CGPoint(x: end.x, y: (labels[0].glyph.maxY + labels[2].frame.minY) / 2)
        view.mouseDragged(with: mouse(.leftMouseDragged, upperLineOnly))
        precondition(showsLabel(labels[0].frame) && !showsLabel(labels[2].frame), "Readings must follow the resized selection")
        view.mouseDragged(with: mouse(.leftMouseDragged, end))
        view.mouseUp(with: mouse(.leftMouseUp, end))
        guard case .image(let saved) = output else { throw CaptureError("Selection did not finish") }
        let savedPNG = try MediaStore.png(saved)
        let cleanPNG = try MediaStore.png(px.image.cropping(to: px.rect(area))!)
        precondition(savedPNG == cleanPNG, "Overlay leaked into screenshot")
        print("PASS  Contextual, tone-marked pinyin; mixed scripts; live labels above 24pt and 48pt text; display edge; clean screenshot")
    }
}
