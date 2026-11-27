import AppKit
import Carbon

private final class BindingButton: NSButton {
    var binding = KeyBinding.capture { didSet { title = binding.title } }
    var listening = false { didSet { title = listening ? "Type shortcut…" : binding.title } }
    var changed: (KeyBinding) -> Void = { _ in }
    override var acceptsFirstResponder: Bool { true }
    override func resignFirstResponder() -> Bool { listening = false; return super.resignFirstResponder() }
    override func mouseDown(with event: NSEvent) { window?.makeFirstResponder(self); listening = true }
    private func bind(_ event: NSEvent) {
        if event.keyCode != UInt16(kVK_Escape) {
            binding = KeyBinding(event)
            changed(binding)
        }
        listening = false
    }
    override func keyDown(with event: NSEvent) {
        if listening { bind(event) } else { window?.makeFirstResponder(self); listening = true }
    }
    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        guard listening else { return super.performKeyEquivalent(with: event) }
        bind(event); return true
    }
}

final class ShortcutEditor: NSObject, NSWindowDelegate {
    let window: NSWindow
    private var draft: Bindings
    private var fields: [BindingButton] = []
    private var global = NSButton()
    private var errorLabel = NSTextField(labelWithString: "")
    private let commit: (Bindings) throws -> Void
    private let onClose: () -> Void

    init(_ bindings: Bindings, commit: @escaping (Bindings) throws -> Void, close: @escaping () -> Void) {
        draft = bindings; self.commit = commit; onClose = close
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 408, height: 318), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        super.init()
        window.title = "OpenCapture Shortcuts"; window.isReleasedWhenClosed = false; window.delegate = self
        guard let view = window.contentView else { return }
        for (i, title) in ["Capture", "Recording mode", "Copy colour or text", "Start or stop recording"].enumerated() {
            let y = CGFloat(260 - i * 39)
            let label = NSTextField(labelWithString: title)
            label.frame = NSRect(x: 22, y: y + 5, width: 192, height: 22); view.addSubview(label)
            let field = BindingButton(frame: NSRect(x: 222, y: y, width: 164, height: 32))
            field.bezelStyle = .rounded
            field.changed = { [weak self] key in
                guard let self else { return }
                switch i {
                case 0: self.draft.capture = key
                case 1: self.draft.record = key
                case 2: self.draft.copy = key
                default: self.draft.control = key
                }
                self.refresh()
            }
            fields.append(field); view.addSubview(field)
        }
        global = NSButton(checkboxWithTitle: "Allow recording shortcut outside capture mode", target: self, action: #selector(toggleGlobal))
        global.frame = NSRect(x: 22, y: 103, width: 367, height: 28); view.addSubview(global)
        errorLabel.frame = NSRect(x: 22, y: 51, width: 365, height: 49)
        errorLabel.font = .systemFont(ofSize: 11); errorLabel.textColor = .secondaryLabelColor
        errorLabel.lineBreakMode = .byWordWrapping; errorLabel.maximumNumberOfLines = 3; view.addSubview(errorLabel)
        let reset = NSButton(title: "Restore Defaults", target: self, action: #selector(resetKeys))
        reset.frame = NSRect(x: 16, y: 13, width: 135, height: 32); view.addSubview(reset)
        let done = NSButton(title: "Save", target: self, action: #selector(save))
        done.frame = NSRect(x: 292, y: 13, width: 100, height: 32); view.addSubview(done)
        refresh()
        window.center()
    }
    private func refresh() {
        for (button, key) in zip(fields, [draft.capture, draft.record ?? draft.capture, draft.copy, draft.control]) { button.binding = key }
        global.state = draft.recordGlobally ? .on : .off
        errorLabel.stringValue = "Click a shortcut, then press its replacement."
    }
    @objc private func toggleGlobal() { draft.recordGlobally = global.state == .on }
    @objc private func resetKeys() { draft = Bindings(); refresh() }
    @objc private func save() {
        let record = draft.record ?? draft.capture
        let invalid = (draft.recordGlobally && record.matches(draft.capture)) ||
            [draft.capture, record].contains { $0.matches(draft.copy) || $0.matches(draft.control) } ||
            draft.copy.matches(draft.control)
        if invalid { errorLabel.stringValue = "Choose distinct shortcuts for actions that can run at the same time."; return }
        if draft.capture.modifiers == 0 || (draft.recordGlobally && record.modifiers == 0) {
            errorLabel.stringValue = "Include a modifier in global capture and recording shortcuts."; return
        }
        do { try commit(draft); window.close() } catch { errorLabel.stringValue = error.localizedDescription }
    }
    func windowWillClose(_ notification: Notification) { onClose() }
}
