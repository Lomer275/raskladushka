import Foundation

public enum RecognitionStatus: String, Error, LocalizedError {
    case ok, keyRejected, busy, timeout, unreachable, failed, missingAddress

    public var message: String {
        switch self {
        case .ok: return "Готово."
        case .keyRejected: return "Ключ не подходит. Проверьте ключ в настройках сервера."
        case .busy: return "Сервер занят. Запись можно отправить ещё раз из меню."
        case .timeout: return "Сервер не ответил вовремя. Запись можно отправить ещё раз из меню."
        case .unreachable: return "Сервер недоступен. Проверьте, что компьютер в сети команды."
        case .failed: return "Не удалось распознать запись."
        case .missingAddress: return "Укажите сервер распознавания в меню значка."
        }
    }
    public var errorDescription: String? { message }
}

public struct RecognitionResult: CustomStringConvertible, CustomDebugStringConvertible, LocalizedError {
    public let status: RecognitionStatus
    public let text: String?
    public var description: String { status.rawValue }
    public var debugDescription: String { description }
    public var errorDescription: String? { status.message }

    public init(_ status: RecognitionStatus, text: String? = nil) {
        self.status = status
        self.text = text
    }
}

public enum RecordingPolicy {
    public static let minimumDuration = 0.3
    public static let maximumDuration = 120.0
    public static func shouldSend(_ duration: TimeInterval) -> Bool {
        duration >= minimumDuration && duration <= maximumDuration
    }
    public static func deadline(_ duration: TimeInterval) -> TimeInterval { 3 + 0.1 * duration }
}

public enum ServerAddress {
    public static func parse(_ value: String) -> URL? {
        let value = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let parts = URLComponents(string: value),
              let scheme = parts.scheme?.lowercased(), ["http", "https"].contains(scheme),
              let host = parts.host, !host.isEmpty, let url = parts.url else { return nil }
        return url
    }
}

public enum AudioRequest {
    public static func make(url: URL, key: String, audio: Data, duration: TimeInterval,
                            wav: Bool = false, boundary: String = UUID().uuidString) -> URLRequest {
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.timeoutInterval = RecordingPolicy.deadline(duration)
        if !key.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
        }
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        var body = Data()
        func append(_ text: String) { body.append(Data(text.utf8)) }
        append("--\(boundary)\r\nContent-Disposition: form-data; name=\"file\"; filename=\"\(wav ? "audio.wav" : "audio.m4a")\"\r\nContent-Type: \(wav ? "audio/wav" : "audio/mp4")\r\n\r\n")
        body.append(audio)
        append("\r\n")
        for (name, value) in [("response_format", "json"), ("temperature", "0.0"), ("language", "ru")] {
            append("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"\r\n\r\n\(value)\r\n")
        }
        append("--\(boundary)--\r\n")
        request.httpBody = body
        return request
    }

    public static func silenceWAV() -> Data {
        let sampleCount = 16000
        var data = Data("RIFF".utf8)
        func u32(_ value: UInt32) { var n = value.littleEndian; withUnsafeBytes(of: &n) { data.append(contentsOf: $0) } }
        func u16(_ value: UInt16) { var n = value.littleEndian; withUnsafeBytes(of: &n) { data.append(contentsOf: $0) } }
        u32(UInt32(36 + sampleCount * 2)); data.append(Data("WAVEfmt ".utf8))
        u32(16); u16(1); u16(1); u32(16000); u32(32000); u16(2); u16(16)
        data.append(Data("data".utf8)); u32(UInt32(sampleCount * 2))
        data.append(Data(count: sampleCount * 2))
        return data
    }
}

// Only sanitized results cross the network boundary; URL errors can contain credentials and URLs.
private final class CompletionGate {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<RecognitionResult, Never>?
    init(_ continuation: CheckedContinuation<RecognitionResult, Never>) { self.continuation = continuation }
    func finish(_ result: RecognitionResult) {
        lock.lock()
        let pending = continuation
        continuation = nil
        lock.unlock()
        pending?.resume(returning: result)
    }
}

public final class RecognitionClient {
    private let session: URLSession
    public init(session: URLSession) { self.session = session }

    public func transcribe(address: String, key: String, audio: Data, duration: TimeInterval,
                           wav: Bool = false, acceptEmptyText: Bool = false) async -> RecognitionResult {
        if address.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return RecognitionResult(.missingAddress) }
        guard let url = ServerAddress.parse(address), RecordingPolicy.shouldSend(duration) else {
            return RecognitionResult(.failed)
        }
        let request = AudioRequest.make(url: url, key: key, audio: audio, duration: duration, wav: wav)
        return await withCheckedContinuation { continuation in
            let gate = CompletionGate(continuation)
            let timer = DispatchSource.makeTimerSource(queue: .global())
            let task = session.dataTask(with: request) { data, response, error in
                timer.cancel()
                gate.finish(Self.result(data: data, response: response, error: error, acceptEmptyText: acceptEmptyText))
            }
            // URLRequest.timeoutInterval alone only limits inactivity, not the complete upload/download.
            timer.schedule(deadline: .now() + RecordingPolicy.deadline(duration))
            timer.setEventHandler {
                gate.finish(RecognitionResult(.timeout))
                task.cancel()
            }
            timer.resume()
            task.resume()
        }
    }

    private static func result(data: Data?, response: URLResponse?, error: Error?, acceptEmptyText: Bool) -> RecognitionResult {
        if let error = error as? URLError {
            if error.code == .timedOut { return RecognitionResult(.timeout) }
            if [.notConnectedToInternet, .cannotConnectToHost, .cannotFindHost, .dnsLookupFailed,
                .networkConnectionLost].contains(error.code) { return RecognitionResult(.unreachable) }
        }
        guard error == nil, let response = response as? HTTPURLResponse else { return RecognitionResult(.failed) }
        switch response.statusCode {
        case 401, 403: return RecognitionResult(.keyRejected)
        case 429, 503: return RecognitionResult(.busy)
        case 200..<300: break
        default: return RecognitionResult(.failed)
        }
        guard let data = data,
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let raw = json["text"] as? String else { return RecognitionResult(.failed) }
        let text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        return text.isEmpty && !acceptEmptyText ? RecognitionResult(.failed) : RecognitionResult(.ok, text: text)
    }
}
