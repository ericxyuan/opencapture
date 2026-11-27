import AppKit
import ServiceManagement
import OSLog
import ScreenCaptureKit

@MainActor final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private var item: NSStatusItem!
    private let menu = NSMenu()
    private let keys = HotKeys()
    private let log = Logger(subsystem: "local.opencapture.mac", category: "App")
    private var bindings = Bindings.load()
    private var overlays: [OverlayPanel] = []
    private var recorder: Recorder?
    private var editor: ShortcutEditor?
    private var freezing = false
    private var exportTask: Task<Void, Never>?
    private var review: CaptureReview?
    private var busy: Bool { freezing || exportTask != nil || review != nil }
    private var terminating = false
    private var stoppingRecording = false
    private var shortcutError: String?
    private var previousApp: NSRunningApplication?
    private var screenAccessDenied = false
    private var selectionMode: CaptureMode = .image
    private var store: MediaStore {
        let path = UserDefaults.standard.string(forKey: "LibraryRoot")
        let fallback = FileManager.default.urls(for: .picturesDirectory, in: .userDomainMask)[0].appendingPathComponent("OpenCapture")
        return MediaStore(root: path.map { URL(fileURLWithPath: $0, isDirectory: true) } ?? fallback)
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        if let index = CommandLine.arguments.firstIndex(of: "--library"), CommandLine.arguments.indices.contains(index + 1) {
            UserDefaults.standard.set(CommandLine.arguments[index + 1], forKey: "LibraryRoot")
        }
        item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        menu.delegate = self; menu.autoenablesItems = false
        item.menu = menu
        updateIcon()
        keys.pressed = { [weak self] id in self?.keyPressed(id) }
        registerKeys()
        if !UserDefaults.standard.bool(forKey: "DidConfigureLogin") {
            do {
                let status = SMAppService.mainApp.status
                if status != .enabled && status != .requiresApproval { try SMAppService.mainApp.register() }
                UserDefaults.standard.set(true, forKey: "DidConfigureLogin")
            } catch {
                log.error("Login registration failed: \(error.localizedDescription, privacy: .public)")
                alert("Couldn’t enable Open at Login", error.localizedDescription)
            }
        }
        NotificationCenter.default.addObserver(self, selector: #selector(screensChanged), name: NSApplication.didChangeScreenParametersNotification, object: nil)
        NSWorkspace.shared.notificationCenter.addObserver(self, selector: #selector(screensChanged), name: NSWorkspace.willSleepNotification, object: nil)
        log.notice("Menu bar ready. Login service: \(SMAppService.mainApp.status.rawValue). Screen access: \(CGPreflightScreenCaptureAccess()). Capture shortcut: \(self.shortcutError == nil).")
        if CommandLine.arguments.contains("--verify-capture") {
            Task {
                do {
                    let frames = try await Desktop.freeze()
                    log.notice("Verified ScreenCaptureKit screenshot on \(frames.count) display(s).")
                } catch {
                    screenAccessDenied = isScreenAccessError(error)
                    log.error("ScreenCaptureKit verification: \(error.localizedDescription, privacy: .public)")
                }
            }
        }
    }

    private func updateIcon() {
        let recording = recorder != nil
        let label = recording ? "OpenCapture — Stop Recording" : "OpenCapture"
        let symbol = recording ? "stop.circle.fill" : "viewfinder"
        item.button?.image = NSImage(systemSymbolName: symbol, accessibilityDescription: label)
        item.button?.image?.isTemplate = true
        item.button?.contentTintColor = recording ? .systemRed : nil
        item.button?.toolTip = label
        item.button?.setAccessibilityLabel(label)
    }

    private func installKeys(_ b: Bindings) throws {
        keys.clear()
        do {
            try keys.add(b.capture, id: 1)
            if b.recordGlobally, let record = b.record { try keys.add(record, id: 2) }
            if recorder != nil { try keys.add(b.control, id: 3) }
        } catch { keys.clear(); throw error }
    }

    private func registerKeys() {
        do { try installKeys(bindings); shortcutError = nil }
        catch { shortcutError = error.localizedDescription; log.error("\(error.localizedDescription, privacy: .public)") }
    }

    private func keyPressed(_ id: UInt32) {
        if recorder != nil { stop(); return }
        guard !busy, editor == nil else { return }
        if !overlays.isEmpty {
            if id == 2 || bindings.record == nil || bindings.record!.matches(bindings.capture) { toggleMode() }
        } else { begin(id == 2 ? .video : .image) }
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        if menu !== self.menu {
            updateClearMenu(menu)
            return
        }
        menu.removeAllItems()
        func add(_ title: String, _ action: Selector, enabled: Bool = true) -> NSMenuItem {
            let line = menu.addItem(withTitle: title, action: action, keyEquivalent: "")
            line.target = self; line.isEnabled = enabled
            return line
        }
        let idle = !busy && recorder == nil && overlays.isEmpty && editor == nil
        let canCapture = idle && (!screenAccessDenied || CGPreflightScreenCaptureAccess())
        if let recorder {
            _ = add(stoppingRecording ? "Finishing Recording…" : "Stop Recording   \(bindings.control.title)", #selector(stop), enabled: !stoppingRecording)
            _ = add("\(recorder.size) recorded", #selector(stop), enabled: false)
        } else {
            _ = add("Capture Screenshot   \(bindings.capture.title)", #selector(screenshot), enabled: canCapture)
            _ = add("Record Area…", #selector(record), enabled: canCapture)
            _ = add("Copy Text from Screen…", #selector(copyText), enabled: canCapture)
            _ = add("Pick Colour…", #selector(pickColour), enabled: canCapture)
        }
        menu.addItem(.separator())
        let library = add("Open Folder", #selector(openFolder))
        let folders = NSMenu()
        for kind in MediaFolder.allCases {
            let line = folders.addItem(withTitle: kind.rawValue, action: #selector(openFolder), keyEquivalent: "")
            line.target = self; line.representedObject = kind.rawValue
        }
        library.submenu = folders
        let clear = add("Clear", #selector(clearClipboard))
        let clearMenu = NSMenu()
        clearMenu.delegate = self; clearMenu.autoenablesItems = false
        clear.submenu = clearMenu
        menu.addItem(.separator())
        let login = SMAppService.mainApp.status
        let line = add(login == .requiresApproval ? "Approve Open at Login…" : "Open at Login", #selector(toggleLogin))
        line.state = login == .enabled ? .on : login == .requiresApproval ? .mixed : .off
        _ = add("Edit Shortcuts…", #selector(editKeys), enabled: idle || editor != nil)
        if let shortcutError { _ = add(shortcutError, #selector(editKeys), enabled: idle) }
        if screenAccessDenied {
            _ = add("Screen Recording Settings…", #selector(openPrivacy))
            _ = add("Retry Screenshot", #selector(retryCapture), enabled: idle)
        }
        menu.addItem(.separator())
        _ = add("Quit OpenCapture", #selector(quit))
    }

    private func updateClearMenu(_ menu: NSMenu) {
        menu.removeAllItems()
        let store = self.store
        for kind in [MediaFolder.images, .videos] {
            let line = menu.addItem(withTitle: "Move Managed \(kind.rawValue) to Trash…", action: #selector(trashMedia), keyEquivalent: "")
            line.target = self; line.representedObject = kind.rawValue
            line.isEnabled = !store.managed(kind).isEmpty
        }
        let cb = menu.addItem(withTitle: "Clear OpenCapture Clipboard", action: #selector(clearClipboard), keyEquivalent: "")
        cb.target = self; cb.isEnabled = NSPasteboard.general.types?.contains(CaptureClipboard.marker) == true
    }

    @objc private func toggleLogin() {
        do {
            switch SMAppService.mainApp.status {
            case .enabled: try SMAppService.mainApp.unregister()
            case .requiresApproval: SMAppService.openSystemSettingsLoginItems()
            default: try SMAppService.mainApp.register()
            }
        } catch { alert("Couldn’t change Open at Login", error.localizedDescription) }
    }
    @objc private func openFolder(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String, let kind = MediaFolder(rawValue: raw) else { return }
        do {
            let folder = try store.folder(kind)
            if !NSWorkspace.shared.open(folder) { throw CaptureError("Finder couldn’t open \(folder.path).") }
        } catch { alert("Couldn’t open the folder", error.localizedDescription) }
    }
    @objc private func clearClipboard() {
        if NSPasteboard.general.types?.contains(CaptureClipboard.marker) == true { NSPasteboard.general.clearContents() }
    }
    @objc private func trashMedia(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String, let kind = MediaFolder(rawValue: raw) else { return }
        let count = store.managed(kind).count
        guard count > 0 else { return }
        let ask = NSAlert()
        ask.messageText = "Move \(count) \(kind.rawValue.lowercased()) to Trash?"
        ask.informativeText = "This clears files created by OpenCapture on this Mac. You can restore them from the Trash."
        ask.addButton(withTitle: "Cancel"); ask.addButton(withTitle: "Move to Trash")
        guard ask.runModal() == .alertSecondButtonReturn else { return }
        do { try store.trash(kind) } catch { alert("Couldn’t finish moving the files", error.localizedDescription) }
    }
    @objc private func editKeys() {
        if let editor { NSApp.activate(ignoringOtherApps: true); editor.window.makeKeyAndOrderFront(nil); return }
        keys.clear()
        editor = ShortcutEditor(bindings, commit: { [weak self] draft in
            guard let self else { return }
            try self.installKeys(draft)
            self.keys.clear()
            self.bindings = draft; draft.save()
        }, close: { [weak self] in self?.editor = nil; self?.registerKeys() })
        NSApp.activate(ignoringOtherApps: true)
        editor?.window.makeKeyAndOrderFront(nil)
    }
    @objc private func openPrivacy() {
        if let settings = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") {
            NSWorkspace.shared.open(settings)
        }
    }
    @objc private func retryCapture() {
        screenAccessDenied = false
        begin(.image)
    }
    private func isScreenAccessError(_ error: Error) -> Bool {
        let failure = error as NSError
        return failure.domain == SCStreamErrorDomain && failure.code == SCStreamError.Code.userDeclined.rawValue
    }
    @objc private func screenshot() { begin(.image) }
    @objc private func record() { begin(.video) }
    @objc private func copyText() { begin(.text) }
    @objc private func pickColour() { begin(.colour) }
    @objc private func stop() { stoppingRecording = true; recorder?.stop() }
    @objc private func quit() { NSApp.terminate(nil) }
    @objc private func screensChanged() {
        if !overlays.isEmpty { closeOverlay() }
        if recorder != nil { stop() }
    }

    private func begin(_ mode: CaptureMode) {
        guard !busy, overlays.isEmpty, recorder == nil, editor == nil else { return }
        if screenAccessDenied && !CGPreflightScreenCaptureAccess() {
            DispatchQueue.main.async { self.item.button?.performClick(nil) }
            return
        }
        freezing = true
        previousApp = NSWorkspace.shared.frontmostApplication
        selectionMode = mode
        Task { [self] in
            do {
                let desktops = try await Desktop.freeze()
                screenAccessDenied = false
                freezing = false
                for desktop in desktops {
                    let panel = OverlayPanel(contentRect: desktop.screen.frame, styleMask: .borderless, backing: .buffered, defer: false)
                    panel.level = .screenSaver; panel.hidesOnDeactivate = false; panel.hasShadow = false
                    panel.acceptsMouseMovedEvents = true; panel.isReleasedWhenClosed = false
                    panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .ignoresCycle]
                    let view = SelectionView(desktop: desktop, mode: mode, bindings: bindings) { [weak self] result in self?.accept(result) }
                    view.switchMode = { [weak self] in self?.toggleMode() }
                    panel.contentView = view
                    panel.setFrame(desktop.screen.frame, display: false)
                    panel.makeFirstResponder(view); overlays.append(panel); panel.orderFrontRegardless()
                }
                NSApp.activate(ignoringOtherApps: true)
                let mouse = NSEvent.mouseLocation
                if let panel = overlays.first(where: { $0.frame.contains(mouse) }), let view = panel.contentView as? SelectionView {
                    panel.makeKeyAndOrderFront(nil)
                    view.move(view.convert(panel.convertPoint(fromScreen: mouse), from: nil))
                }
            } catch {
                defer { freezing = false }
                if isScreenAccessError(error) {
                    guard !screenAccessDenied else { return }
                    screenAccessDenied = true
                    let message = NSAlert()
                    message.messageText = "Allow OpenCapture to capture your screen"
                    message.informativeText = "Enable OpenCapture in Screen & System Audio Recording. If macOS offers Quit & Reopen, choose it to apply the change."
                    message.addButton(withTitle: "Open System Settings"); message.addButton(withTitle: "Cancel")
                    NSApp.activate(ignoringOtherApps: true)
                    if message.runModal() == .alertFirstButtonReturn { openPrivacy() }
                } else { alert("Couldn’t capture the screen", error.localizedDescription) }
            }
        }
    }
    private func toggleMode() {
        selectionMode = selectionMode == .video ? .image : .video
        for panel in overlays { (panel.contentView as? SelectionView)?.mode = selectionMode }
    }
    private func closeOverlay() {
        for panel in overlays { panel.orderOut(nil); panel.close() }
        overlays.removeAll()
        if let app = previousApp, app != .current { _ = app.activate(options: []) }
        previousApp = nil
    }
    private func accept(_ result: SelectionResult?) {
        guard !overlays.isEmpty else { return }
        closeOverlay()
        guard let result else { return }
        do {
            switch result {
            case .colour(let hex): CaptureClipboard.text(hex)
            case .image(let image):
                review = CaptureReview(image: image) { [weak self] accepted in
                    guard let self else { return }
                    self.review = nil
                    if accepted { self.saveScreenshot(image) }
                }
                review?.show()
            case .text(let image, let cached):
                let dest = store
                exportTask = Task {
                    defer { exportTask = nil }
                    do {
                        let text = try await Task.detached(priority: .userInitiated) {
                            let result = try cached ?? OCR.read(image)
                            guard !result.text.isEmpty else { throw CaptureError("Select an area containing legible text and try again.") }
                            _ = try dest.savePNG(MediaStore.png(image), text: result.text)
                            return result.text
                        }.value
                        CaptureClipboard.text(text)
                    } catch { alert("Couldn’t copy text", error.localizedDescription) }
                }
            case .video(let desktop, let area):
                let session = try Recorder(store: store) { [weak self] outcome in
                    guard let self else { return }
                    self.recorder = nil; self.stoppingRecording = false; self.updateIcon(); self.registerKeys()
                    switch outcome {
                    case .success(let url): CaptureClipboard.copy(url.dataRepresentation, type: .fileURL)
                    case .failure(let error): self.alert("Couldn’t finish recording", error.localizedDescription)
                    }
                    if self.terminating { NSApp.reply(toApplicationShouldTerminate: true) }
                }
                recorder = session; updateIcon(); registerKeys()
                Task { await session.start(desktop, area: area) }
            }
        } catch { alert("OpenCapture couldn’t complete the capture", error.localizedDescription) }
    }
    private func saveScreenshot(_ image: CGImage) {
        let dest = store
        exportTask = Task {
            defer { exportTask = nil }
            do {
                let data = try await Task.detached(priority: .userInitiated) {
                    let data = try MediaStore.png(image)
                    _ = try dest.savePNG(data)
                    return data
                }.value
                CaptureClipboard.copy(data, type: .png)
            } catch { alert("Couldn’t save the screenshot", error.localizedDescription) }
        }
    }

    private func alert(_ title: String, _ message: String) {
        let alert = NSAlert()
        alert.messageText = title; alert.informativeText = message
        NSApp.activate(ignoringOtherApps: true); alert.runModal()
    }
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        review?.confirm()
        if recorder != nil { terminating = true; stop(); return .terminateLater }
        if let exportTask {
            Task {
                await exportTask.value
                NSApp.reply(toApplicationShouldTerminate: true)
            }
            return .terminateLater
        }
        return .terminateNow
    }
}

@main enum OpenCapture {
    @MainActor static func main() {
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.setActivationPolicy(.accessory)
        app.delegate = delegate
        withExtendedLifetime(delegate) { app.run() }
    }
}
