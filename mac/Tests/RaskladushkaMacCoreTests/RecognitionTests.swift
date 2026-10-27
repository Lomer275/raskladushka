import Foundation
import XCTest
@testable import RaskladushkaMacCore

private final class StubProtocol: URLProtocol {
    static var handler: ((URLRequest, StubProtocol) -> Void)?
    static var calls = 0
    private let lock = NSRecursiveLock()
    private var stopped = false
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        Self.calls += 1
        Self.handler?(request, self)
    }
    override func stopLoading() { lock.lock(); stopped = true; lock.unlock() }
    func reply(_ code: Int = 200, _ body: String = "{\"text\":\"  TEXT-SENTINEL \\n\"}") {
        lock.lock(); defer { lock.unlock() }
        guard !stopped else { return }
        client?.urlProtocol(self, didReceive: HTTPURLResponse(url: request.url!, statusCode: code,
                                                            httpVersion: nil, headerFields: nil)!, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data(body.utf8))
        client?.urlProtocolDidFinishLoading(self)
    }
    func fail(_ code: URLError.Code) {
        client?.urlProtocol(self, didFailWithError: URLError(code, userInfo: [NSURLErrorFailingURLErrorKey: request.url!]))
    }
    func drip(_ remaining: Int) {
        lock.lock(); defer { lock.unlock() }
        guard !stopped else { return }
        if remaining == 5 {
            client?.urlProtocol(self, didReceive: HTTPURLResponse(url: request.url!, statusCode: 200,
                                                                httpVersion: nil, headerFields: nil)!, cacheStoragePolicy: .notAllowed)
        }
        client?.urlProtocol(self, didLoad: Data(" ".utf8))
        if remaining > 0 {
            DispatchQueue.global().asyncAfter(deadline: .now() + 1) { self.drip(remaining - 1) }
        } else { client?.urlProtocolDidFinishLoading(self) }
    }
}

final class RecognitionTests: XCTestCase {
    private var session: URLSession!
    private var client: RecognitionClient!
    private let address = "http://addr-sentinel.example/x"
    private let key = "KEY-SENTINEL"

    override func setUp() {
        super.setUp()
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubProtocol.self]
        session = URLSession(configuration: configuration)
        client = RecognitionClient(session: session)
        StubProtocol.calls = 0
        StubProtocol.handler = nil
    }
    override func tearDown() {
        session.invalidateAndCancel()
        StubProtocol.handler = nil
        super.tearDown()
    }
    private func send(address: String? = nil, duration: Double = 20) async -> RecognitionResult {
        await client.transcribe(address: address ?? self.address, key: key, audio: Data([0, 1, 254, 255]), duration: duration)
    }
    private func assertPrivate(_ value: RecognitionResult, file: StaticString = #filePath, line: UInt = #line) {
        for representation in [String(describing: value), String(reflecting: value), value.localizedDescription,
                               String(describing: value.status), value.status.localizedDescription,
                               String(describing: value as Error), (value as NSError).localizedDescription] {
            for sentinel in [key, address, "TEXT-SENTINEL"] {
                XCTAssertFalse(representation.contains(sentinel), file: file, line: line)
            }
        }
    }
    func testHTTPTableAndExactMessages() async {
        let cases: [(Int, RecognitionStatus, String)] = [
            (401, .keyRejected, "Ключ не подходит. Проверьте ключ в настройках сервера."),
            (403, .keyRejected, "Ключ не подходит. Проверьте ключ в настройках сервера."),
            (429, .busy, "Сервер занят. Запись можно отправить ещё раз из меню."),
            (503, .busy, "Сервер занят. Запись можно отправить ещё раз из меню."),
            (500, .failed, "Не удалось распознать запись."),
            (404, .failed, "Не удалось распознать запись.")
        ]
        for (code, expected, message) in cases {
            StubProtocol.handler = { _, stub in stub.reply(code, "{\"text\":\"TEXT-SENTINEL\",\"key\":\"KEY-SENTINEL\",\"url\":\"http://addr-sentinel.example/x\"}") }
            let result = await send()
            XCTAssertEqual(result.status, expected)
            XCTAssertEqual(result.status.message, message)
            XCTAssertNil(result.text)
            assertPrivate(result)
        }
    }
    func testNetworkTableAndExactMessages() async {
        for code in [URLError.notConnectedToInternet, .cannotConnectToHost, .cannotFindHost, .dnsLookupFailed, .networkConnectionLost] {
            StubProtocol.handler = { _, stub in stub.fail(code) }
            let result = await send()
            XCTAssertEqual(result.status, .unreachable)
            XCTAssertEqual(result.status.message, "Сервер недоступен. Проверьте, что компьютер в сети команды.")
            assertPrivate(result)
        }
        StubProtocol.handler = { _, stub in stub.fail(.timedOut) }
        let timeout = await send()
        XCTAssertEqual(timeout.status, .timeout)
        XCTAssertEqual(timeout.status.message, "Сервер не ответил вовремя. Запись можно отправить ещё раз из меню.")
        assertPrivate(timeout)
        for code in [URLError.badServerResponse, .cancelled, .secureConnectionFailed] {
            StubProtocol.handler = { _, stub in stub.fail(code) }
            let result = await send()
            XCTAssertEqual(result.status, .failed)
            assertPrivate(result)
        }
    }
    func testResponseParsingAndPrivacy() async {
        for body in ["garbage TEXT-SENTINEL KEY-SENTINEL http://addr-sentinel.example/x", "{}", "[]", "null",
                     "{\"text\":null}", "{\"text\":42}", "{\"text\":\"\"}", "{\"text\":\" \\n\\t \"}"] {
            StubProtocol.handler = { _, stub in stub.reply(200, body) }
            let result = await send()
            XCTAssertEqual(result.status, .failed)
            XCTAssertEqual(result.status.message, "Не удалось распознать запись.")
            assertPrivate(result)
        }
        StubProtocol.handler = { _, stub in stub.reply() }
        let result = await send()
        XCTAssertEqual(result.status, .ok)
        XCTAssertEqual(result.text, "TEXT-SENTINEL")
        assertPrivate(result)
    }
    func testEmptyAndInvalidAddressDoNotCallNetwork() async {
        let result = await send(address: " \n ")
        XCTAssertEqual(result.status, .missingAddress)
        XCTAssertEqual(result.status.message, "Укажите сервер распознавания в меню значка.")
        assertPrivate(result)
        let invalid = await send(address: "file:///tmp/audio")
        XCTAssertEqual(invalid.status, .failed)
        XCTAssertEqual(StubProtocol.calls, 0)
    }
    func testEmptyTextAcceptedOnlyWhenRequested() async {
        StubProtocol.handler = { _, stub in stub.reply(200, "{\"text\":\"\"}") }
        let accepted = await client.transcribe(address: address, key: key, audio: AudioRequest.silenceWAV(),
                                               duration: 1, wav: true, acceptEmptyText: true)
        XCTAssertEqual(accepted.status, .ok)
        XCTAssertEqual(accepted.text, "")

        let rejected = await send()
        XCTAssertEqual(rejected.status, .failed)
        XCTAssertNil(rejected.text)
    }
    func testMissingTextRejectedEvenWhenEmptyTextAccepted() async {
        StubProtocol.handler = { _, stub in stub.reply(200, "{}") }
        let result = await client.transcribe(address: address, key: key, audio: AudioRequest.silenceWAV(),
                                             duration: 1, wav: true, acceptEmptyText: true)
        XCTAssertEqual(result.status, .failed)
        XCTAssertNil(result.text)
    }
    func testAuthorizationHeader() {
        for key in ["", "   ", " \n\t "] {
            let request = AudioRequest.make(url: URL(string: address)!, key: key, audio: Data(), duration: 1)
            XCTAssertNil(request.value(forHTTPHeaderField: "Authorization"))
        }
        let request = AudioRequest.make(url: URL(string: address)!, key: "k", audio: Data(), duration: 1)
        XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer k")
    }
    func testAddressRules() {
        for value in ["http://localhost:8080/inference", " HTTPS://example.com/x ", "http://127.0.0.1/x", "http://[::1]:80/x"] {
            XCTAssertNotNil(ServerAddress.parse(value))
        }
        for value in ["", "example.com", "/inference", "ftp://example.com", "http://", "https:///x"] {
            XCTAssertNil(ServerAddress.parse(value))
        }
    }
    func testMultipartThroughSession() async {
        StubProtocol.handler = { request, stub in
            XCTAssertEqual(request.httpMethod, "POST")
            XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer KEY-SENTINEL")
            var body = request.httpBody ?? Data()
            if let stream = request.httpBodyStream {
                stream.open(); defer { stream.close() }
                var buffer = [UInt8](repeating: 0, count: 4096)
                while stream.hasBytesAvailable {
                    let n = stream.read(&buffer, maxLength: buffer.count)
                    if n <= 0 { break }
                    body.append(contentsOf: buffer.prefix(n))
                }
            }
            XCTAssertNotNil(body.range(of: Data([0, 1, 254, 255])))
            let text = String(decoding: body, as: UTF8.self)
            for part in ["name=\"file\"; filename=\"audio.m4a\"", "Content-Type: audio/mp4",
                         "name=\"response_format\"\r\n\r\njson", "name=\"temperature\"\r\n\r\n0.0",
                         "name=\"language\"\r\n\r\nru"] { XCTAssertTrue(text.contains(part)) }
            let boundary = request.value(forHTTPHeaderField: "Content-Type")!.components(separatedBy: "boundary=")[1]
            XCTAssertTrue(text.hasPrefix("--\(boundary)\r\n"))
            XCTAssertTrue(text.hasSuffix("--\(boundary)--\r\n"))
            stub.reply()
        }
        let result = await send()
        XCTAssertEqual(result.status, .ok)
    }
    func testDeadlinesAndThresholds() async {
        XCTAssertEqual(RecordingPolicy.deadline(0), 3)
        XCTAssertEqual(RecordingPolicy.deadline(20), 5)
        XCTAssertEqual(RecordingPolicy.deadline(120), 15)
        XCTAssertFalse(RecordingPolicy.shouldSend(0.299))
        XCTAssertTrue(RecordingPolicy.shouldSend(0.3))
        XCTAssertTrue(RecordingPolicy.shouldSend(120))
        XCTAssertFalse(RecordingPolicy.shouldSend(120.001))
        StubProtocol.handler = { _, stub in stub.reply() }
        let short = await send(duration: 0.299)
        let long = await send(duration: 120.001)
        XCTAssertEqual(short.status, .failed)
        XCTAssertEqual(long.status, .failed)
        XCTAssertEqual(StubProtocol.calls, 0)
        for duration in [0.3, 120.0] {
            let result = await send(duration: duration)
            XCTAssertEqual(result.status, .ok)
        }
        XCTAssertEqual(StubProtocol.calls, 2)
    }
    func testDeadlineCoversWholeResponseDespiteActivity() async {
        StubProtocol.handler = { _, stub in stub.drip(5) }
        let start = ProcessInfo.processInfo.systemUptime
        let result = await send(duration: 0.3)
        let elapsed = ProcessInfo.processInfo.systemUptime - start
        XCTAssertEqual(result.status, .timeout)
        XCTAssertGreaterThanOrEqual(elapsed, 2.9)
        XCTAssertLessThan(elapsed, 4.5)
        assertPrivate(result)
    }
    func testInMemoryWAV() {
        let audio = AudioRequest.silenceWAV()
        XCTAssertEqual(audio.count, 32044)
        XCTAssertEqual(String(decoding: audio.prefix(4), as: UTF8.self), "RIFF")
        XCTAssertEqual(Array(audio[22..<24]), [1, 0])
        XCTAssertEqual(Array(audio[24..<28]), [128, 62, 0, 0])
        XCTAssertTrue(audio.dropFirst(44).allSatisfy { $0 == 0 })
        let request = AudioRequest.make(url: URL(string: address)!, key: key, audio: audio, duration: 1, wav: true)
        let body = String(decoding: request.httpBody!, as: UTF8.self)
        XCTAssertTrue(body.contains("filename=\"audio.wav\""))
        XCTAssertTrue(body.contains("Content-Type: audio/wav"))
    }
    func testHoldGesture() {
        var gesture = HoldGesture()
        XCTAssertEqual(gesture.rightOption(down: true, otherKeyDown: false, busy: false, permitted: true), .start)
        XCTAssertEqual(gesture.otherKey(), .discard)
        XCTAssertEqual(gesture.rightOption(down: false, otherKeyDown: false, busy: false, permitted: true), .none)
        XCTAssertEqual(gesture.rightOption(down: true, otherKeyDown: true, busy: false, permitted: true), .none)
        _ = gesture.rightOption(down: false, otherKeyDown: false, busy: false, permitted: true)
        XCTAssertEqual(gesture.rightOption(down: true, otherKeyDown: false, busy: true, permitted: true), .none)
        XCTAssertEqual(gesture.rightOption(down: true, otherKeyDown: false, busy: false, permitted: true), .none)
        _ = gesture.rightOption(down: false, otherKeyDown: false, busy: false, permitted: true)
        XCTAssertEqual(gesture.rightOption(down: true, otherKeyDown: false, busy: false, permitted: false), .missingPermission)
        _ = gesture.rightOption(down: false, otherKeyDown: false, busy: false, permitted: true)
        XCTAssertEqual(gesture.rightOption(down: true, otherKeyDown: false, busy: false, permitted: true), .start)
        XCTAssertEqual(gesture.reachedLimit(), .finish)
        XCTAssertEqual(gesture.rightOption(down: false, otherKeyDown: false, busy: false, permitted: true), .none)
        XCTAssertEqual(gesture.rightOption(down: true, otherKeyDown: false, busy: false, permitted: true), .start)
        XCTAssertEqual(gesture.rightOption(down: false, otherKeyDown: false, busy: false, permitted: true), .finish)
    }
}
