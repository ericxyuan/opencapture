import AppKit
import ImageIO

// Runs without a display server or capture permission. All media is synthetic.
@main struct OfflineSmoke {
    static func main() async throws {
        let size = CGSize(width: 96, height: 64)
        let context = CGContext(data: nil, width: 192, height: 128, bitsPerComponent: 8, bytesPerRow: 0,
                                space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)!
        context.setFillColor(gray: 1, alpha: 1)
        context.fill(CGRect(x: 0, y: 0, width: 192, height: 128))
        context.setFillColor(red: 1, green: 0, blue: 0, alpha: 1)
        context.fill(CGRect(x: 20, y: 60, width: 80, height: 40))
        let pixels = try Pixels(context.makeImage()!, pointSize: size)
        let bounds = CGRect(origin: .zero, size: size)
        let red = CGPoint(x: 15, y: 19)
        precondition(pixels.hex(red) == "#FF0000")
        precondition(pixels.hex(CGPoint(x: -1, y: 1000)) == "#FFFFFF")
        precondition(pixels.panel(at: red, inside: bounds) == CGRect(x: 10, y: 14, width: 40, height: 20))
        let crop = pixels.rect(CGRect(x: -1, y: 4.5, width: 21.25, height: 12))
        precondition(crop == CGRect(x: 0, y: 9, width: 41, height: 24))
        let png = try MediaStore.png(pixels.image.cropping(to: crop)!)
        let source = CGImageSourceCreateWithData(png as CFData, nil)!
        let decoded = CGImageSourceCreateImageAtIndex(source, 0, nil)!
        precondition(decoded.width == 41 && decoded.height == 24)
        print("PASS  Retina sampling, edge clamping, hover geometry, clipped crop and PNG round-trip")

        let id = UUID().uuidString
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(id, isDirectory: true)
        let defaults = UserDefaults(suiteName: id)!
        let store = MediaStore(root: root, defaults: defaults)
        defer { try? FileManager.default.removeItem(at: root); defaults.removePersistentDomain(forName: id) }
        let saved = try await withThrowingTaskGroup(of: URL.self) { group in
            for _ in 0..<24 {
                group.addTask {
                    _ = store.managed(.images)
                    return try store.savePNG(png)
                }
            }
            var files = Set<URL>()
            for try await file in group { files.insert(file) }
            return files
        }
        precondition(saved.count == 24 && Set(store.managed(.images)) == saved)
        for file in saved {
            let contents = try Data(contentsOf: file)
            precondition(contents == png)
        }
        let changed = saved.first!
        try Data([0]).write(to: changed, options: .atomic)
        precondition(Set(store.managed(.images)) == saved.subtracting([changed]))
        let renamed = saved.first { $0 != changed }!
        let temp = renamed.appendingPathExtension("tmp")
        try FileManager.default.moveItem(at: renamed, to: temp)
        try FileManager.default.moveItem(at: temp, to: renamed)
        precondition(Set(store.managed(.images)) == saved.subtracting([changed, renamed]))
        for expected in ["test file.png", "test file (2).png", "test file (3).png"] {
            let url = try store.savePNG(png, text: "test:/file")
            precondition(url.lastPathComponent == expected)
        }
        print("PASS  Concurrent storage and index reads, collision naming, edit and rename protection")
    }
}
