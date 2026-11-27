import AppKit
import ImageIO
import UniformTypeIdentifiers
import Vision

struct CaptureError: LocalizedError {
    let errorDescription: String?
    init(_ message: String) { errorDescription = message }
}

enum MediaFolder: String, CaseIterable {
    case images = "Captures", videos = "Videos", text = "Copied Text"
}

struct MediaStore {
    // Menu validation and background PNG publication share the ownership index.
    private static let indexLock = NSLock()
    private static let fallbackMoveLock = NSLock()
    let root: URL
    let defaults: UserDefaults

    init(root: URL, defaults: UserDefaults = .standard) {
        self.root = root
        self.defaults = defaults
    }

    func folder(_ kind: MediaFolder) throws -> URL {
        let url = root.appendingPathComponent(kind.rawValue, isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    static func timestamp(_ date: Date, milliseconds: Bool = false) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = milliseconds ? "HH-mm-ss-SSS '---' dd-MM-yy" : "HH-mm-ss '---' dd-MM-yy"
        return formatter.string(from: date)
    }

    static func filename(for text: String) -> String {
        let forbidden = CharacterSet(charactersIn: "/\\:*?\"<>|").union(.controlCharacters)
        let words = text.components(separatedBy: forbidden).joined(separator: " ")
            .split(whereSeparator: { $0.isWhitespace }).joined(separator: " ")
        var name = ""
        for character in words {
            guard name.utf8.count + String(character).utf8.count <= 200 else { break }
            name.append(character)
        }
        name = name.trimmingCharacters(in: CharacterSet(charactersIn: ". "))
        return name.isEmpty ? "Copied Text" : name
    }

    // Publishing with a move in the destination directory avoids partially visible files
    // and lets another writer win a name collision without overwriting its work.
    func publish(_ staging: URL, in kind: MediaFolder, ext: String, text: String? = nil) throws -> URL {
        let directory = try folder(kind)
        let date = Date()
        let base = text.map(Self.filename) ?? Self.timestamp(date)
        lazy var collisionBase = text == nil ? Self.timestamp(date, milliseconds: true) : base
        var attempt = 0
        while true {
            var name = attempt == 0 ? base : collisionBase
            let suffix = text == nil ? attempt - 1 : attempt
            if suffix > 0 { name += " (\(suffix + 1))" }
            let url = directory.appendingPathComponent(name).appendingPathExtension(ext)
            if try moveIfAvailable(staging, to: url) {
                if kind != .text { register(url, in: kind) }
                return url
            }
            attempt += 1
        }
    }

    private func moveIfAvailable(_ source: URL, to target: URL) throws -> Bool {
        if renamex_np(source.path, target.path, UInt32(RENAME_EXCL)) == 0 { return true }
        let code = errno
        if code == EEXIST { return false }
        guard code == ENOTSUP || code == EINVAL || code == EXDEV else {
            throw NSError(domain: NSPOSIXErrorDomain, code: Int(code), userInfo: [NSFilePathErrorKey: target.path])
        }
        // Preserve FileManager support for providers without exclusive rename.
        // Serialize its existence check and move across OpenCapture writers.
        Self.fallbackMoveLock.lock()
        defer { Self.fallbackMoveLock.unlock() }
        do {
            try FileManager.default.moveItem(at: source, to: target)
            return true
        } catch let error as NSError where error.domain == NSCocoaErrorDomain && error.code == NSFileWriteFileExistsError {
            return false
        }
    }

    static func png(_ image: CGImage) throws -> Data {
        let data = NSMutableData()
        guard let dest = CGImageDestinationCreateWithData(data, UTType.png.identifier as CFString, 1, nil)
        else { throw CaptureError("Couldn’t create a PNG image.") }
        CGImageDestinationAddImage(dest, image, nil)
        guard CGImageDestinationFinalize(dest) else { throw CaptureError("Couldn’t encode the PNG image.") }
        return data as Data
    }

    func savePNG(_ data: Data, text: String? = nil) throws -> URL {
        let kind: MediaFolder = text == nil ? .images : .text
        let staging = try folder(kind).appendingPathComponent(".\(UUID().uuidString).png")
        defer { try? FileManager.default.removeItem(at: staging) }
        try data.write(to: staging, options: .withoutOverwriting)
        return try publish(staging, in: kind, ext: "png", text: text)
    }

    // Only exact files published by this Mac can be cleared. A change of inode,
    // status-change time (including rename), or length retires the entry.
    private func identity(_ url: URL) -> String? {
        var info = stat()
        guard lstat(url.path, &info) == 0, (info.st_mode & S_IFMT) == S_IFREG else { return nil }
        return "\(info.st_dev)/\(info.st_ino)/\(info.st_ctimespec.tv_sec)/\(info.st_ctimespec.tv_nsec)/\(info.st_size)"
    }

    private func indexKey(_ kind: MediaFolder) -> String { "managed.\(root.path).\(kind.rawValue)" }

    private func register(_ url: URL, in kind: MediaFolder) {
        Self.indexLock.lock()
        defer { Self.indexLock.unlock() }
        let key = indexKey(kind)
        var entries = defaults.dictionary(forKey: key) as? [String: String] ?? [:]
        entries[url.lastPathComponent] = identity(url)
        defaults.set(entries, forKey: key)
    }

    func managed(_ kind: MediaFolder) -> [URL] {
        Self.indexLock.lock()
        defer { Self.indexLock.unlock() }
        let key = indexKey(kind)
        guard let entries = defaults.dictionary(forKey: key) as? [String: String] else { return [] }
        let directory = root.appendingPathComponent(kind.rawValue)
        let matching = entries.filter { name, stamp in
            name == (name as NSString).lastPathComponent && identity(directory.appendingPathComponent(name)) == stamp
        }
        if matching.count != entries.count { defaults.set(matching, forKey: key) }
        return matching.keys.map { directory.appendingPathComponent($0) }
    }

    func trash(_ kind: MediaFolder) throws {
        for url in managed(kind) { try FileManager.default.trashItem(at: url, resultingItemURL: nil) }
        _ = managed(kind)
    }
}

enum CaptureClipboard {
    static let marker = NSPasteboard.PasteboardType("local.opencapture.media")
    static func copy(_ data: Data, type: NSPasteboard.PasteboardType, to board: NSPasteboard = .general) {
        let item = NSPasteboardItem()
        item.setData(data, forType: type)
        item.setString("1", forType: marker)
        board.clearContents()
        board.writeObjects([item])
    }
    static func text(_ string: String) {
        let board = NSPasteboard.general
        board.clearContents()
        board.setString(string, forType: .string)
    }
}

struct RecognizedText {
    let text: String
    let boxes: [CGRect] // normalized, origin at bottom left (Vision coordinates)
    let pinyin: [PinyinAnnotation]
}

enum OCR {
    static func read(_ image: CGImage, includePinyin: Bool = false) throws -> RecognizedText {
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = true
        request.automaticallyDetectsLanguage = true
        let languages = try request.supportedRecognitionLanguages()
        let preferred = Locale.preferredLanguages + ["en-US", "zh-Hans", "zh-Hant"]
        var selected: [String] = []
        for language in preferred {
            let match = languages.first { $0 == language } ?? languages.first { language.hasPrefix($0) }
            if let match, !selected.contains(match) { selected.append(match) }
        }
        if !selected.isEmpty { request.recognitionLanguages = selected }
        try VNImageRequestHandler(cgImage: image).perform([request])
        let results = request.results ?? []
        var lines: [String] = []
        var pinyin: [PinyinAnnotation] = []
        for result in results {
            guard let candidate = result.topCandidates(1).first else { continue }
            lines.append(candidate.string)
            if includePinyin { pinyin += Pinyin.annotations(for: candidate, lineBox: result.boundingBox) }
        }
        let text = lines.joined(separator: "\n").applyingTransform(StringTransform("Traditional-Simplified"), reverse: false)
        return RecognizedText(text: text ?? lines.joined(separator: "\n"), boxes: results.map(\.boundingBox), pinyin: pinyin)
    }
}
