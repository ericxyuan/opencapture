import AppKit
import CoreText
import Vision

struct PinyinAnnotation {
    let syllable: String
    let box: CGRect // Character bounds in the same normalized coordinates as Vision.
}

enum Pinyin {
    private static let chinese = Locale(identifier: "zh_CN")

    /// Preserve indices into the original OCR line, including around punctuation and emoji.
    static func readings(in line: String) -> [String.Index: String] {
        var result: [String.Index: String] = [:]
        let runs = line.split { $0.unicodeScalars.first?.properties.isIdeographic != true }
        for run in runs {
            let original = String(run)
            let simplified = original.applyingTransform(StringTransform("Traditional-Simplified"), reverse: false)
            let text = simplified.flatMap { $0.count == run.count ? $0 : nil } ?? original
            var syllables = romanize(text)
            // The Mandarin tokenizer supplies contextual readings for words such as 银行
            // and 音乐. Its transcription joins the syllables without spaces.
            let fullRange = CFRange(location: 0, length: text.utf16.count)
            if let tokenizer = CFStringTokenizerCreate(nil, text as CFString, fullRange, kCFStringTokenizerUnitWord, chinese as CFLocale) {
                var cursor = text.startIndex, position = 0
                while CFStringTokenizerAdvanceToNextToken(tokenizer) != [] {
                    let token = CFStringTokenizerGetCurrentTokenRange(tokenizer)
                    guard let range = Range(NSRange(location: token.location, length: token.length), in: text),
                          let transcription = CFStringTokenizerCopyCurrentTokenAttribute(tokenizer, kCFStringTokenizerAttributeLatinTranscription) as? String else { continue }
                    position += text.distance(from: cursor, to: range.lowerBound)
                    let end = position + text[range].count
                    apply(transcription, to: &syllables, in: position..<end)
                    position = end
                    cursor = range.upperBound
                }
            }
            for (index, syllable) in zip(run.indices, syllables) where isRomanized(syllable) {
                result[index] = syllable
            }
        }
        return result
    }

    private static func romanize(_ text: String) -> [String] {
        let phrase = text.applyingTransform(.mandarinToLatin, reverse: false) ?? ""
        let syllables = phrase.split(whereSeparator: \.isWhitespace).map(String.init)
        if syllables.count == text.count { return syllables }
        return text.map { String($0).applyingTransform(.mandarinToLatin, reverse: false) ?? "" }
    }

    private static func isRomanized(_ text: String) -> Bool {
        !text.isEmpty && text.folding(options: .diacriticInsensitive, locale: chinese).utf8.allSatisfy { (97...122).contains($0) }
    }

    private static func apply(_ transcription: String, to syllables: inout [String], in range: Range<Int>) {
        let word = transcription.decomposedStringWithCanonicalMapping
            .replacingOccurrences(of: "v", with: "u\u{0308}", options: .literal)
            .precomposedStringWithCanonicalMapping
        var rest = word[...]
        var left = range.lowerBound, right = range.upperBound
        // Match known syllables from both ends. Preserve the tokenizer's tones.
        func sameReading(_ a: Substring, _ b: String) -> Bool {
            a.compare(b, options: .diacriticInsensitive) == .orderedSame
        }
        while left + 1 < right {
            let prefix = rest.prefix(syllables[left].count)
            if !prefix.isEmpty && sameReading(prefix, syllables[left]) {
                syllables[left] = String(prefix); rest = rest.dropFirst(prefix.count); left += 1
                continue
            }
            let suffix = rest.suffix(syllables[right - 1].count)
            guard !suffix.isEmpty && sameReading(suffix, syllables[right - 1]) else { break }
            syllables[right - 1] = String(suffix); rest = rest.dropLast(suffix.count); right -= 1
        }
        if left + 1 == right && isRomanized(String(rest)) { syllables[left] = String(rest) }
    }

    static func annotations(for candidate: VNRecognizedText, lineBox: CGRect) -> [PinyinAnnotation] {
        let text = candidate.string
        let readings = readings(in: text)
        guard !readings.isEmpty else { return [] }
        let indices = Array(text.indices) + [text.endIndex]
        let count = indices.count - 1
        let boxes: [CGRect?] = (0..<count).map { i in
            let box = try? candidate.boundingBox(for: indices[i]..<indices[i + 1])?.boundingBox
            return box?.isEmpty == false ? box : nil
        }
        // Some recognition revisions return a whole word for every character in it.
        // Use script-aware glyph advances only when Vision didn't resolve the character.
        lazy var advances: [CGFloat] = {
            let font = CTFontCreateWithName("PingFangSC-Regular" as CFString, 24, nil)
            let line = CTLineCreateWithAttributedString(NSAttributedString(string: text, attributes: [.font: font]))
            return indices.map { CTLineGetOffsetForStringIndex(line, $0.utf16Offset(in: text), nil) }
        }()
        var result: [PinyinAnnotation] = []
        var start = 0
        while start < count {
            var end = start + 1
            while end < count && boxes[end] == boxes[start] { end += 1 }
            let groupBox = boxes[start] ?? lineBox
            for i in start..<end {
                guard let syllable = readings[indices[i]] else { continue }
                var box = groupBox
                if end - start > 1 || boxes[start] == nil {
                    let lo = advances[boxes[start] == nil ? 0 : start]
                    let hi = advances[boxes[start] == nil ? count : end]
                    guard hi > lo else { continue }
                    box.origin.x += groupBox.width * (advances[i] - lo) / (hi - lo)
                    box.size.width = groupBox.width * (advances[i + 1] - advances[i]) / (hi - lo)
                }
                if !box.isEmpty { result.append(PinyinAnnotation(syllable: syllable, box: box)) }
            }
            start = end
        }
        return result
    }
}

/// A tone-marked reading in view points. The character's width limits long syllables
/// so adjacent readings do not run together, even on a densely typeset line.
struct PinyinLabel {
    let glyph: CGRect
    let frame: CGRect
    let font: NSFont
    let text: String
    private let textSize: CGSize
    private let textWidth: CGFloat

    init?(_ annotation: PinyinAnnotation, crop: CGRect, screen: CGRect) {
        let b = annotation.box
        glyph = CGRect(x: crop.minX + b.minX * crop.width, y: crop.minY + (1 - b.maxY) * crop.height,
                       width: b.width * crop.width, height: b.height * crop.height)
        guard glyph.width >= 2, glyph.height >= 2 else { return nil }
        text = annotation.syllable
        let padding = min(glyph.width * 0.10, glyph.height * 0.055)
        let available = glyph.width * 0.97 - 2 * padding
        let nominal = NSFont.systemFont(ofSize: glyph.height * 0.44, weight: .semibold)
        let width = (text as NSString).size(withAttributes: [.font: nominal]).width
        // Condense longer readings slightly first so neighbouring tone marks line up.
        font = .systemFont(ofSize: nominal.pointSize * min(1, available / max(1, width * 0.72)), weight: .semibold)
        textSize = (text as NSString).size(withAttributes: [.font: font])
        textWidth = min(available, textSize.width)
        let w = textWidth + 2 * padding
        let h = textSize.height + padding
        let gap = max(0.5, glyph.height * 0.025)
        let above = glyph.minY - gap - h
        let y = above >= screen.minY ? above : min(glyph.maxY + gap, screen.maxY - h)
        frame = CGRect(x: max(screen.minX, min(glyph.midX - w / 2, screen.maxX - w)),
                       y: max(screen.minY, y), width: w, height: h)
    }

    func draw() {
        let radius = min(frame.height * 0.18, 4)
        NSColor(calibratedWhite: 0.08, alpha: 0.94).setFill()
        NSBezierPath(roundedRect: frame, xRadius: radius, yRadius: radius).fill()
        guard let context = NSGraphicsContext.current?.cgContext else { return }
        context.saveGState()
        context.translateBy(x: frame.midX - textWidth / 2, y: frame.midY - textSize.height / 2)
        context.scaleBy(x: textWidth / textSize.width, y: 1)
        (text as NSString).draw(at: .zero, withAttributes: [.font: font, .foregroundColor: NSColor.white])
        context.restoreGState()
    }
}
