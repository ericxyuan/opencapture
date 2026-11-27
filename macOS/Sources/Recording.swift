import AppKit
import AVFoundation
import ScreenCaptureKit

final class Recorder: NSObject, SCRecordingOutputDelegate, SCStreamDelegate {
    private var stream: SCStream?
    private var output: SCRecordingOutput?
    private var border: NSPanel?
    private var stopping = false
    private var started = false
    private var finished = false
    private let staging: URL
    private let store: MediaStore
    private let onFinish: (Result<URL, Error>) -> Void

    init(store: MediaStore, onFinish: @escaping (Result<URL, Error>) -> Void) throws {
        self.store = store
        self.onFinish = onFinish
        self.staging = try store.folder(.videos).appendingPathComponent(".OpenCapture-\(UUID().uuidString).mp4")
    }

    @MainActor func start(_ desktop: FrozenDisplay, area: CGRect) async {
        let config = SCStreamConfiguration()
        config.sourceRect = area
        let physical = desktop.pixels.rect(area)
        let ratio = min(1, 4096 / max(physical.width, physical.height))
        config.width = max(2, 2 * Int(physical.width * ratio / 2))
        config.height = max(2, 2 * Int(physical.height * ratio / 2))
        config.minimumFrameInterval = CMTime(value: 1, timescale: 60)
        config.showsCursor = true
        config.capturesAudio = false
        let settings = SCRecordingOutputConfiguration()
        settings.outputURL = staging
        settings.outputFileType = .mp4
        settings.videoCodecType = .h264
        let stream = SCStream(filter: desktop.filter, configuration: config, delegate: self)
        let output = SCRecordingOutput(configuration: settings, delegate: self)
        self.stream = stream
        self.output = output
        do {
            try stream.addRecordingOutput(output)
            try await stream.startCapture()
            guard !finished else { return }
            started = true
            if stopping { stopStream(); return }
            let frame = desktop.screen.frame
            let rect = CGRect(x: frame.minX + area.minX, y: frame.maxY - area.maxY, width: area.width, height: area.height)
            let panel = NSPanel(contentRect: rect.insetBy(dx: -2, dy: -2), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
            panel.backgroundColor = .clear; panel.isOpaque = false; panel.hasShadow = false
            panel.ignoresMouseEvents = true; panel.level = .floating
            panel.sharingType = .none
            panel.hidesOnDeactivate = false
            panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .ignoresCycle]
            let view = NSView(frame: CGRect(origin: .zero, size: panel.frame.size))
            view.wantsLayer = true
            view.layer?.borderColor = NSColor.systemRed.cgColor
            view.layer?.borderWidth = 2
            panel.contentView = view; panel.orderFrontRegardless()
            border = panel
        } catch { end(.failure(error)) }
    }

    func stop() {
        guard !stopping, !finished else { return }
        stopping = true
        if started { stopStream() }
    }
    private func stopStream() {
        stream?.stopCapture { [weak self] error in
            if let error { DispatchQueue.main.async { self?.end(.failure(error)) } }
            // The output delegate signals when the MP4 is safe to move.
        }
    }
    func recordingOutputDidFinishRecording(_ recordingOutput: SCRecordingOutput) {
        DispatchQueue.main.async {
            guard !self.finished else { return }
            self.end(Result { try self.store.publish(self.staging, in: .videos, ext: "mp4") })
        }
    }
    func recordingOutput(_ recordingOutput: SCRecordingOutput, didFailWithError error: Error) {
        DispatchQueue.main.async { self.end(.failure(error)) }
    }
    func stream(_ stream: SCStream, didStopWithError error: Error) {
        DispatchQueue.main.async { self.end(.failure(error)) }
    }
    private func end(_ result: Result<URL, Error>) {
        guard !finished else { return }
        finished = true
        border?.orderOut(nil); border = nil
        if case .failure = result {
            stream?.stopCapture(completionHandler: { _ in })
            // Keep recoverable footage if recording or publication was interrupted.
            if let length = try? staging.resourceValues(forKeys: [.fileSizeKey]).fileSize, length > 0 {
                NSWorkspace.shared.activateFileViewerSelecting([staging])
            } else { try? FileManager.default.removeItem(at: staging) }
        }
        output = nil; stream = nil
        onFinish(result)
    }
    var size: String { ByteCountFormatter.string(fromByteCount: Int64(output?.recordedFileSize ?? 0), countStyle: .file) }
}
