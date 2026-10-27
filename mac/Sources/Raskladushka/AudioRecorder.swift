import Foundation
import AVFoundation
import RaskladushkaMacCore

final class AudioRecorder {
    private let directory = FileManager.default.temporaryDirectory.appendingPathComponent("io.github.lomer275.raskladushka-audio", isDirectory: true)
    private var recorder: AVAudioRecorder?
    private var file: URL?

    func cleanAtLaunch() throws {
        let fm = FileManager.default
        if fm.fileExists(atPath: directory.path) {
            let values = try directory.resourceValues(forKeys: [.isSymbolicLinkKey, .isDirectoryKey])
            guard values.isSymbolicLink != true, values.isDirectory == true else { throw RecognitionStatus.failed }
            for item in try fm.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil) {
                try fm.removeItem(at: item)
            }
        } else {
            try fm.createDirectory(at: directory, withIntermediateDirectories: false,
                                   attributes: [.posixPermissions: 0o700])
        }
        try fm.setAttributes([.posixPermissions: 0o700], ofItemAtPath: directory.path)
    }

    func start() throws {
        discard()
        guard file == nil else { throw RecognitionStatus.failed }
        let url = directory.appendingPathComponent(UUID().uuidString).appendingPathExtension("m4a")
        file = url
        do {
            let value = try AVAudioRecorder(url: url, settings: [
                AVFormatIDKey: kAudioFormatMPEG4AAC,
                AVSampleRateKey: 16000,
                AVNumberOfChannelsKey: 1,
                AVEncoderBitRateKey: 32000
            ])
            recorder = value
            guard value.record(forDuration: RecordingPolicy.maximumDuration) else { throw RecognitionStatus.failed }
        } catch {
            discard()
            throw RecognitionStatus.failed
        }
    }

    func stop() throws -> Data {
        recorder?.stop()
        recorder = nil
        guard let url = file else { throw RecognitionStatus.failed }
        // A failed deletion must not allow the caller to start a network operation.
        do {
            let audio = try Data(contentsOf: url)
            try FileManager.default.removeItem(at: url)
            file = nil
            return audio
        } catch {
            discard()
            throw RecognitionStatus.failed
        }
    }

    func discard() {
        recorder?.stop()
        recorder = nil
        if let url = file {
            do { try FileManager.default.removeItem(at: url); file = nil }
            catch {
                if !FileManager.default.fileExists(atPath: url.path) { file = nil }
            }
        }
    }
    deinit { discard() }
}
