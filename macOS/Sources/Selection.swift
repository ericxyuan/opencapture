import AppKit
import Carbon

enum CaptureMode {
    case image, video, text, colour
    var title: String {
        switch self {
        case .image: return "Screenshot"
        case .video: return "Record area"
        case .text: return "Copy text"
        case .colour: return "Pick colour"
        }
    }
}

enum SelectionResult {
    case image(CGImage), video(FrozenDisplay, CGRect), text(CGImage, RecognizedText?), colour(String)
}

final class OverlayPanel: NSPanel {
    override var canBecomeKey: Bool { true }
}

final class SelectionView: NSView {
    let desktop: FrozenDisplay
    let bindings: Bindings
    let finish: (SelectionResult?) -> Void
    var switchMode: () -> Void = {}
    var mode: CaptureMode { didSet { pending = nil; needsDisplay = true } }
    private let picture: NSImage
    private var pointer = CGPoint.zero
    private var anchor: CGPoint?
    private var dragged = false
    private var hover: CGRect?
    private var pending: CGRect?
    private var sensitivity: CGFloat = 1
    private var adjustingPointer = false
    private var recognized: (area: CGRect, result: RecognizedText, labels: [PinyinLabel], labelBounds: CGRect)?
    private var scanning = false
    private var tracking: NSTrackingArea?
    private var isInside = false
    private var completed = false
    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }
    override var isOpaque: Bool { true }

    init(desktop: FrozenDisplay, mode: CaptureMode, bindings: Bindings, finish: @escaping (SelectionResult?) -> Void) {
        self.desktop = desktop; self.mode = mode; self.bindings = bindings; self.finish = finish
        picture = NSImage(cgImage: desktop.pixels.image, size: desktop.screen.frame.size)
        super.init(frame: CGRect(origin: .zero, size: desktop.screen.frame.size))
    }
    required init?(coder: NSCoder) { fatalError() }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let tracking { removeTrackingArea(tracking) }
        let area = NSTrackingArea(rect: bounds, options: [.activeAlways, .mouseEnteredAndExited, .mouseMoved, .cursorUpdate], owner: self)
        addTrackingArea(area)
        tracking = area
    }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func cursorUpdate(with event: NSEvent) { NSCursor.crosshair.set() }
    override func mouseEntered(with event: NSEvent) {
        window?.makeKey()
        move(convert(event.locationInWindow, from: nil))
    }
    override func mouseExited(with event: NSEvent) {
        if anchor == nil { isInside = false; needsDisplay = true }
    }
    func move(_ p: CGPoint, dragging: Bool = false) {
        let next = CGPoint(x: min(max(0, p.x), bounds.width - 1 / desktop.pixels.sx),
                           y: min(max(0, p.y), bounds.height - 1 / desktop.pixels.sy))
        let oldHUD = hudRect, oldSelection = isInside ? selection : nil
        isInside = true
        pointer = next
        if let anchor {
            if dragging && !dragged && hypot(pointer.x - anchor.x, pointer.y - anchor.y) >= 3 { dragged = true }
        } else { hover = desktop.zone(at: pointer) }
        setNeedsDisplay(oldHUD.insetBy(dx: -3, dy: -3))
        setNeedsDisplay(hudRect.insetBy(dx: -3, dy: -3))
        if oldSelection != selection {
            if let oldSelection { setNeedsDisplay(oldSelection.insetBy(dx: -3, dy: -3)) }
            if let selection { setNeedsDisplay(selection.insetBy(dx: -3, dy: -3)) }
            invalidateLabels()
        }
    }
    private func follow(_ event: NSEvent, dragging: Bool = false) {
        var p = convert(event.locationInWindow, from: nil)
        if sensitivity != 1, isInside, !adjustingPointer {
            p = CGPoint(x: pointer.x + (p.x - pointer.x) * sensitivity, y: pointer.y + (p.y - pointer.y) * sensitivity)
            move(p, dragging: dragging)
            warp()
        } else { move(p, dragging: dragging) }
        adjustingPointer = false
    }
    private func warp() {
        let screen = desktop.screen.frame
        let primaryTop = NSScreen.screens.first?.frame.maxY ?? screen.maxY
        CGWarpMouseCursorPosition(CGPoint(x: screen.minX + pointer.x, y: primaryTop - screen.maxY + pointer.y))
    }
    override func mouseMoved(with event: NSEvent) { follow(event) }
    override func scrollWheel(with event: NSEvent) {
        if abs(event.scrollingDeltaY) > 0 {
            sensitivity = min(4, max(0.25, sensitivity + (event.scrollingDeltaY > 0 ? 0.25 : -0.25)))
            setNeedsDisplay(hudRect)
        }
    }
    override func otherMouseDown(with event: NSEvent) {
        if event.buttonNumber == 2 { sensitivity = 1; setNeedsDisplay(hudRect) }
    }
    override func rightMouseDown(with event: NSEvent) { complete(nil) }
    override func mouseDown(with event: NSEvent) {
        window?.makeKey()
        move(convert(event.locationInWindow, from: nil))
        if mode == .colour { complete(.colour(desktop.pixels.hex(pointer))); return }
        invalidateLabels()
        let hadPendingArea = pending != nil
        pending = nil; anchor = pointer; dragged = false; recognized = nil
        if hadPendingArea { needsDisplay = true }
    }
    override func mouseDragged(with event: NSEvent) {
        guard anchor != nil else { return }
        follow(event, dragging: true)
        if dragged { scheduleOCR() }
    }
    override func mouseUp(with event: NSEvent) {
        guard anchor != nil, !completed else { return }
        follow(event)
        let area = selection
        let manual = dragged
        anchor = nil; dragged = false
        guard let area, !area.isEmpty else { needsDisplay = true; return }
        if mode == .video && manual { pending = area; scheduleOCR(); needsDisplay = true }
        else { choose(area) }
    }
    private var selection: CGRect? {
        if let anchor, dragged {
            return CGRect(x: min(anchor.x, pointer.x), y: min(anchor.y, pointer.y),
                          width: abs(anchor.x - pointer.x), height: abs(anchor.y - pointer.y)).intersection(bounds)
        }
        return pending ?? hover
    }
    private func complete(_ result: SelectionResult?) {
        guard !completed else { return }
        completed = true
        finish(result)
    }
    func choose(_ area: CGRect, textOnly: Bool = false) {
        let px = desktop.pixels
        if mode == .video && !textOnly { complete(.video(desktop, area)); return }
        guard let image = px.image.cropping(to: px.rect(area)) else { return }
        if mode == .text || textOnly {
            complete(.text(image, recognized?.area == area ? recognized?.result : nil))
        } else { complete(.image(image)) }
    }
    private var readingArea: CGRect? { dragged ? selection : pending }
    private func invalidateLabels() {
        if let extent = recognized?.labelBounds, !extent.isNull { setNeedsDisplay(extent.insetBy(dx: -1, dy: -1)) }
    }
    private func scheduleOCR() {
        guard !scanning, !completed, let area = readingArea, recognized?.area != area,
              area.width > 4, area.height > 4 else { return }
        let pixels = desktop.pixels
        let pixelRect = pixels.rect(area)
        guard let crop = pixels.image.cropping(to: pixelRect) else { return }
        let cropInPoints = pixelRect.applying(CGAffineTransform(scaleX: 1 / pixels.sx, y: 1 / pixels.sy))
        scanning = true
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            let result = (try? OCR.read(crop, includePinyin: true)) ?? RecognizedText(text: "", boxes: [], pinyin: [])
            DispatchQueue.main.async { [weak self] in
                guard let self else { return }
                self.scanning = false
                guard self.window?.isVisible == true, let current = self.readingArea, !self.completed else { return }
                self.invalidateLabels()
                let labels = result.pinyin.compactMap { PinyinLabel($0, crop: cropInPoints, screen: self.bounds) }
                self.recognized = (area, result, labels, labels.reduce(CGRect.null) { $0.union($1.frame) })
                self.invalidateLabels()
                self.setNeedsDisplay(area)
                if current != area { self.scheduleOCR() }
            }
        }
    }
    func handle(_ event: NSEvent) -> Bool {
        let key = KeyBinding(event)
        if Int(event.keyCode) == kVK_Escape { complete(nil); return true }
        if key.matches(bindings.copy) {
            if (dragged || mode == .text), let area = selection { choose(area, textOnly: true) }
            else { complete(.colour(desktop.pixels.hex(pointer))) }
            return true
        }
        if key.matches(bindings.record ?? bindings.capture) { if !event.isARepeat { switchMode() }; return true }
        if key.matches(bindings.control), mode == .video {
            if let area = selection { choose(area) }
            return true
        }
        if event.modifierFlags.intersection([.command, .option, .control]).isEmpty {
            var dx: CGFloat = 0, dy: CGFloat = 0
            switch Int(event.keyCode) {
            case kVK_LeftArrow, kVK_ANSI_A: dx = -1
            case kVK_RightArrow, kVK_ANSI_D: dx = 1
            case kVK_UpArrow, kVK_ANSI_W: dy = -1
            case kVK_DownArrow, kVK_ANSI_S: dy = 1
            case kVK_Return: if let area = selection { choose(area) }; return true
            default: return false
            }
            adjustingPointer = true
            move(CGPoint(x: pointer.x + dx / desktop.pixels.sx, y: pointer.y + dy / desktop.pixels.sy))
            warp()
            if dragged { scheduleOCR() }
            return true
        }
        return false
    }
    override func keyDown(with event: NSEvent) { if !handle(event) { super.keyDown(with: event) } }
    override func performKeyEquivalent(with event: NSEvent) -> Bool { handle(event) || super.performKeyEquivalent(with: event) }

    private var hudRect: CGRect {
        let w: CGFloat = min(300, bounds.width), h: CGFloat = 92, gap: CGFloat = 18
        let right = pointer.x + gap, left = pointer.x - gap - w
        let below = pointer.y + gap, above = pointer.y - gap - h
        let base = CGPoint(x: right + w <= bounds.maxX ? right : left, y: below + h <= bounds.maxY ? below : above)
        func inside(_ x: CGFloat, _ y: CGFloat) -> CGRect {
            CGRect(x: min(max(0, x), bounds.maxX - w), y: min(max(0, y), bounds.maxY - h), width: w, height: h)
        }
        let first = inside(base.x, base.y)
        guard let area = selection, first.intersects(area) else { return first }
        if !dragged && area.insetBy(dx: 48, dy: 48).contains(pointer) && w * h <= area.width * area.height * 0.12 { return first }
        let candidates = [inside(area.maxX + gap, base.y), inside(area.minX - w - gap, base.y),
                          inside(base.x, area.maxY + gap), inside(base.x, area.minY - h - gap)]
        return candidates.filter { !$0.intersects(area) }.min {
            hypot($0.midX - pointer.x, $0.midY - pointer.y) < hypot($1.midX - pointer.x, $1.midY - pointer.y)
        } ?? first
    }
    override func draw(_ dirtyRect: NSRect) {
        picture.draw(in: bounds, from: .zero, operation: .copy, fraction: 1, respectFlipped: true, hints: nil)
        guard isInside else { return }
        let accent: NSColor = mode == .video ? .systemRed : .systemCyan
        if let area = selection, mode != .colour {
            accent.withAlphaComponent(0.10).setFill(); area.fill()
            accent.setStroke()
            let border = NSBezierPath(rect: area.insetBy(dx: 0.5, dy: 0.5))
            border.lineWidth = 1; border.stroke()
            if dragged, let recognized, recognized.area == area {
                accent.withAlphaComponent(0.33).setFill()
                for b in recognized.result.boxes {
                    CGRect(x: area.minX + b.minX * area.width, y: area.minY + (1 - b.maxY) * area.height,
                           width: b.width * area.width, height: b.height * area.height).fill()
                }
            }
            if let active = readingArea, let recognized, dirtyRect.intersects(recognized.labelBounds) {
                let selected = active.insetBy(dx: -1, dy: -1)
                for label in recognized.labels where dirtyRect.intersects(label.frame) && selected.contains(label.glyph) {
                    label.draw()
                }
            }
        }
        let hud = hudRect
        guard dirtyRect.intersects(hud) else { return }
        let card = NSBezierPath(roundedRect: hud, xRadius: 12, yRadius: 12)
        NSColor(calibratedWhite: 0.085, alpha: 0.96).setFill(); card.fill()
        NSColor.white.withAlphaComponent(0.22).setStroke(); card.stroke()
        let px = desktop.pixels
        let col = Int(pointer.x * px.sx), row = Int(pointer.y * px.sy)
        let magnifier = CGRect(x: hud.minX + 10, y: hud.minY + 13, width: 66, height: 66)
        NSGraphicsContext.saveGraphicsState()
        NSBezierPath(roundedRect: magnifier, xRadius: 4, yRadius: 4).addClip()
        NSGraphicsContext.current?.imageInterpolation = .none
        let sample = CGRect(x: col - 5, y: row - 5, width: 11, height: 11)
        let part = sample.intersection(CGRect(x: 0, y: 0, width: px.image.width, height: px.image.height))
        if let detail = px.image.cropping(to: part) {
            let target = CGRect(x: magnifier.minX + (part.minX - sample.minX) * 6, y: magnifier.minY + (part.minY - sample.minY) * 6, width: part.width * 6, height: part.height * 6)
            NSImage(cgImage: detail, size: part.size).draw(in: target, from: .zero, operation: .copy, fraction: 1, respectFlipped: true, hints: nil)
        }
        NSColor.white.setStroke()
        NSBezierPath(rect: CGRect(x: magnifier.midX - 3, y: magnifier.midY - 3, width: 6, height: 6)).stroke()
        NSGraphicsContext.restoreGraphicsState()
        func label(_ value: String, _ offset: CGFloat, _ font: NSFont, _ color: NSColor) {
            (value as NSString).draw(at: CGPoint(x: hud.minX + 86, y: hud.minY + offset), withAttributes: [.font: font, .foregroundColor: color])
        }
        let dimensions = selection.map { px.rect($0) }
        let size = dimensions.map { "\(Int($0.width)) × \(Int($0.height))" } ?? "\(col), \(row)"
        label(mode.title, 9, .systemFont(ofSize: 13, weight: .semibold), .white)
        label("\(px.hex(pointer))   \(size)", 29, .monospacedSystemFont(ofSize: 11, weight: .regular), .white)
        let hint: String
        switch mode {
        case .video: hint = pending != nil ? "\(bindings.control.title) to start recording" : "Click a target or drag an area"
        case .text: hint = "Select text to copy"
        case .colour: hint = "Click to copy HEX"
        case .image: hint = "Click a target or drag an area"
        }
        label(hint, 48, .systemFont(ofSize: 11), .white)
        let footer = dragged ? "\(bindings.copy.title) to copy text" : "Esc to cancel"
        label("\(footer)   ·   \(Int(sensitivity * 100))%", 65, .systemFont(ofSize: 10), .lightGray)
    }
}
