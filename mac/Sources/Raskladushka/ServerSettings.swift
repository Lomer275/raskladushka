import AppKit
import Security
import RaskladushkaMacCore

enum ServerKey {
    private static var query: [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: "io.github.lomer275.raskladushka",
         kSecAttrAccount as String: "server-key"]
    }
    static func read() throws -> String {
        var request = query
        request[kSecReturnData as String] = true
        request[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(request as CFDictionary, &result)
        if status == errSecItemNotFound { return "" }
        guard status == errSecSuccess, let data = result as? Data,
              let text = String(data: data, encoding: .utf8) else { throw RecognitionStatus.keyRejected }
        return text
    }
    static func save(_ key: String) throws {
        if key.isEmpty {
            let status = SecItemDelete(query as CFDictionary)
            guard status == errSecSuccess || status == errSecItemNotFound else { throw RecognitionStatus.failed }
            return
        }
        let attributes: [String: Any] = [kSecValueData as String: Data(key.utf8),
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly]
        let status = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
        if status == errSecItemNotFound {
            var item = query
            attributes.forEach { item[$0.key] = $0.value }
            guard SecItemAdd(item as CFDictionary, nil) == errSecSuccess else { throw RecognitionStatus.failed }
        } else if status != errSecSuccess { throw RecognitionStatus.failed }
    }
}

@MainActor
final class ServerSettings: NSObject, NSWindowDelegate {
    private var window: NSWindow!
    private let address = NSTextField()
    private let key = NSSecureTextField()
    private let status = NSTextField(wrappingLabelWithString: "")
    private var check: NSButton!
    private let client: RecognitionClient
    private var checking = false
    var onChecking: ((Bool) -> Void)?
    var canCheck: (() -> Bool)?
    var onOutcome: ((RecognitionStatus, TimeInterval, TimeInterval) -> Void)?

    init(client: RecognitionClient) {
        self.client = client
        super.init()
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 560, height: 250),
                          styleMask: [.titled, .closable], backing: .buffered, defer: false)
        window.title = "Сервер распознавания"
        window.isReleasedWhenClosed = false
        window.delegate = self
        let view = window.contentView!
        let label1 = NSTextField(labelWithString: "Адрес")
        let label2 = NSTextField(labelWithString: "Ключ")
        check = NSButton(title: "Проверить", target: self, action: #selector(testServer))
        let save = NSButton(title: "Сохранить", target: self, action: #selector(saveServer))
        for (element, frame) in [
            (label1, NSRect(x: 20, y: 210, width: 80, height: 22)),
            (address, NSRect(x: 100, y: 205, width: 440, height: 26)),
            (label2, NSRect(x: 20, y: 166, width: 80, height: 22)),
            (key, NSRect(x: 100, y: 161, width: 440, height: 26)),
            (check!, NSRect(x: 100, y: 115, width: 120, height: 32)),
            (save, NSRect(x: 230, y: 115, width: 130, height: 32)),
            (status, NSRect(x: 20, y: 15, width: 520, height: 85))
        ] as [(NSView, NSRect)] { element.frame = frame; view.addSubview(element) }
        window.center()
    }

    func show() {
        if !window.isVisible {
            address.stringValue = UserDefaults.standard.string(forKey: "server-address") ?? ""
            do { key.stringValue = try ServerKey.read(); status.stringValue = "" }
            catch { key.stringValue = ""; status.stringValue = "Не удалось подготовить ключ сервера." }
        }
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }

    private func fields() -> (String, String)? {
        let value = address.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        if !value.isEmpty && ServerAddress.parse(value) == nil {
            status.stringValue = "Адрес должен начинаться с http:// или https://."
            return nil
        }
        return (value, key.stringValue)
    }
    @objc private func saveServer() {
        guard let (url, secret) = fields() else { return }
        do {
            try ServerKey.save(secret)
            UserDefaults.standard.set(url, forKey: "server-address")
            status.stringValue = "Сохранено."
        } catch { status.stringValue = "Не удалось сохранить настройки сервера." }
    }
    @objc private func testServer() {
        guard !checking, canCheck?() != false, let (url, secret) = fields() else { return }
        guard !url.isEmpty else {
            status.stringValue = "Адрес должен начинаться с http:// или https://."
            return
        }
        checking = true; check.isEnabled = false; onChecking?(true)
        status.stringValue = "Распознаю…"
        Task { @MainActor in
            let start = Date()
            let result = await client.transcribe(address: url, key: secret, audio: AudioRequest.silenceWAV(),
                                                 duration: 1, wav: true, acceptEmptyText: true)
            let elapsed = Date().timeIntervalSince(start)
            onOutcome?(result.status, 1, elapsed)
            status.stringValue = result.status == .ok
                ? String(format: "Сервер ответил за %.1f с.", elapsed) : result.status.message
            checking = false; check.isEnabled = true; onChecking?(false)
        }
    }
    func windowWillClose(_ notification: Notification) { key.stringValue = "" }
}
