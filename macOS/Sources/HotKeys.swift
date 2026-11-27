import AppKit
import Carbon

struct KeyBinding: Codable, Equatable {
    let code: UInt32
    let modifiers: UInt32
    let key: String

    static let capture = KeyBinding(code: UInt32(kVK_ANSI_Q), modifiers: UInt32(optionKey), key: "Q")
    static let copy = KeyBinding(code: UInt32(kVK_ANSI_C), modifiers: UInt32(cmdKey), key: "C")
    static let control = KeyBinding(code: UInt32(kVK_Space), modifiers: 0, key: "Space")

    var title: String {
        var result = ""
        for (mask, label) in [(controlKey, "⌃"), (optionKey, "⌥"), (shiftKey, "⇧"), (cmdKey, "⌘")] {
            if modifiers & UInt32(mask) != 0 { result += label }
        }
        return result + key
    }

    init(code: UInt32, modifiers: UInt32, key: String) {
        self.code = code; self.modifiers = modifiers; self.key = key
    }

    init(_ event: NSEvent) {
        code = UInt32(event.keyCode)
        var flags: UInt32 = 0
        for (flag, mask): (NSEvent.ModifierFlags, Int) in [(.command, cmdKey), (.option, optionKey), (.control, controlKey), (.shift, shiftKey)] {
            if event.modifierFlags.contains(flag) { flags |= UInt32(mask) }
        }
        modifiers = flags
        switch Int(event.keyCode) {
        case kVK_Space: key = "Space"
        case kVK_Return: key = "Return"
        case kVK_Tab: key = "Tab"
        case kVK_Delete: key = "Delete"
        case kVK_LeftArrow: key = "←"
        case kVK_RightArrow: key = "→"
        case kVK_UpArrow: key = "↑"
        case kVK_DownArrow: key = "↓"
        default: key = event.characters(byApplyingModifiers: [])?.uppercased() ?? "Key \(code)"
        }
    }

    func matches(_ other: KeyBinding) -> Bool { code == other.code && modifiers == other.modifiers }
}

struct Bindings: Codable {
    var capture = KeyBinding.capture
    var copy = KeyBinding.copy
    var control = KeyBinding.control
    var record: KeyBinding? // nil uses the capture key within the overlay
    var recordGlobally = false
    static func load() -> Self {
        guard let data = UserDefaults.standard.data(forKey: "bindings"),
              let result = try? JSONDecoder().decode(Self.self, from: data) else { return Self() }
        return result
    }
    func save() { UserDefaults.standard.set(try? JSONEncoder().encode(self), forKey: "bindings") }
}

final class HotKeys {
    private var refs: [EventHotKeyRef] = []
    private var handler: EventHandlerRef?
    private let signature: OSType = 0x4F436170 // OCap
    var pressed: (UInt32) -> Void = { _ in }

    init() {
        var event = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, event, context in
            guard let context, let event else { return OSStatus(eventNotHandledErr) }
            let manager = Unmanaged<HotKeys>.fromOpaque(context).takeUnretainedValue()
            var key = EventHotKeyID()
            let status = GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                                           nil, MemoryLayout<EventHotKeyID>.size, nil, &key)
            guard status == noErr, key.signature == manager.signature else { return OSStatus(eventNotHandledErr) }
            manager.pressed(key.id)
            return noErr
        }, 1, &event, Unmanaged.passUnretained(self).toOpaque(), &handler)
    }

    func clear() {
        for ref in refs { UnregisterEventHotKey(ref) }
        refs.removeAll()
    }

    func add(_ binding: KeyBinding, id: UInt32) throws {
        var ref: EventHotKeyRef?
        let status = RegisterEventHotKey(binding.code, binding.modifiers, EventHotKeyID(signature: signature, id: id),
                                         GetApplicationEventTarget(), 0, &ref)
        guard status == noErr, let ref else { throw CaptureError("\(binding.title) is unavailable. Choose another shortcut in Edit Shortcuts.") }
        refs.append(ref)
    }

    deinit {
        clear()
        if let handler { RemoveEventHandler(handler) }
    }
}
