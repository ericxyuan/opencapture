import AppKit

/// Hold a screenshot in memory until it is accepted or the review expires.
@MainActor final class CaptureReview: NSObject {
    private let panel: NSPanel
    private var timer: Timer?
    private var completion: ((Bool) -> Void)?

    init(image: CGImage, completion: @escaping (Bool) -> Void) {
        self.completion = completion
        panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 294, height: 92),
                        styleMask: [.titled, .nonactivatingPanel], backing: .buffered, defer: false)
        super.init()
        panel.title = "Review screenshot"
        panel.isFloatingPanel = true
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        let preview = NSImageView(frame: NSRect(x: 12, y: 10, width: 80, height: 72))
        preview.image = NSImage(cgImage: image, size: .zero)
        preview.imageScaling = .scaleProportionallyUpOrDown
        preview.setAccessibilityLabel("Screenshot preview")
        panel.contentView?.addSubview(preview)
        let hint = NSTextField(labelWithString: "Saving in 2 seconds")
        hint.frame = NSRect(x: 103, y: 54, width: 174, height: 20)
        panel.contentView?.addSubview(hint)
        for (title, action, x) in [("Discard", #selector(discard), 98), ("Confirm", #selector(confirm), 190)] {
            let button = NSButton(title: title, target: self, action: action)
            button.frame = NSRect(x: x, y: 12, width: 90, height: 32)
            panel.contentView?.addSubview(button)
        }
    }

    func show() {
        let screen = NSScreen.screens.first { $0.frame.contains(NSEvent.mouseLocation) } ?? NSScreen.main
        if let frame = screen?.visibleFrame {
            panel.setFrameTopLeftPoint(NSPoint(x: frame.maxX - panel.frame.width - 16, y: frame.maxY - 16))
        }
        panel.orderFrontRegardless()
        let timer = Timer(timeInterval: 2, target: self, selector: #selector(confirm), userInfo: nil, repeats: false)
        self.timer = timer
        RunLoop.main.add(timer, forMode: .common)
    }

    @objc func confirm() { finish(true) }
    @objc func discard() { finish(false) }

    private func finish(_ accept: Bool) {
        timer?.invalidate()
        timer = nil
        let callback = completion
        completion = nil
        panel.orderOut(nil)
        callback?(accept)
    }
}
