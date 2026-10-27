import AppKit
import AVFoundation
import CoreGraphics
import ApplicationServices
import os
import RaskladushkaMacCore

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private let logger = Logger(subsystem: "io.github.lomer275.raskladushka", category: "recognition")
    private let client = RecognitionClient(session: URLSession(configuration: .ephemeral))
    private let recorder = AudioRecorder()
    private let paster = TextPaster()
    private var settings: ServerSettings!
    private var statusItem: NSStatusItem!
    private let menu = NSMenu()
    private var retry: NSMenuItem!
    private var permissionItems: [NSMenuItem] = []
    private var tap: CFMachPort?
    private var tapSource: CFRunLoopSource?
    private var permissionTimer: Timer?
    private var limitTimer: Timer?
    private var started: TimeInterval?
    private var lastAudio: Data?
    private var lastDuration: TimeInterval = 0
    private var busy = false
    private var gesture = HoldGesture()
    private var panel: NSPanel?
    private var dismissPanel: DispatchWorkItem?
    private var storageReady = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        do { try recorder.cleanAtLaunch(); storageReady = true } catch { storageReady = false }
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.menu = menu
        menu.delegate = self
        menu.autoenablesItems = false
        menu.addItem(withTitle: "Сервер распознавания…", action: #selector(openSettings), keyEquivalent: "").target = self
        retry = menu.addItem(withTitle: "Отправить последнюю запись ещё раз", action: #selector(retryLast), keyEquivalent: "")
        retry.target = self
        menu.addItem(.separator())
        menu.addItem(withTitle: "Выход", action: #selector(quit), keyEquivalent: "q").target = self
        settings = ServerSettings(client: client)
        settings.canCheck = { [weak self] in self.map { !$0.busy && $0.started == nil } ?? false }
        settings.onChecking = { [weak self] value in self?.busy = value; self?.updateStatus() }
        settings.onOutcome = { [weak self] status, duration, elapsed in self?.log(status, duration, elapsed) }
        updateStatus()
        refreshPermissions()
        if !CGPreflightListenEventAccess() {
            showMessage("Раскладушка не слышит правый Option. Разрешите «Мониторинг ввода» — строка в меню значка.", seconds: 10)
        }
        Permissions.request()
        installTap()
        permissionTimer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.refreshPermissions()
                if self?.tap == nil { self?.installTap() }
            }
        }
    }

    private func installTap() {
        guard tap == nil, CGPreflightListenEventAccess() else { return }
        let mask = (CGEventMask(1) << CGEventType.flagsChanged.rawValue)
            | (CGEventMask(1) << CGEventType.keyDown.rawValue)
        let callback: CGEventTapCallBack = { _, type, event, context in
            guard let context = context else { return Unmanaged.passUnretained(event) }
            let owner = Unmanaged<AppDelegate>.fromOpaque(context).takeUnretainedValue()
            // The tap is installed on the main run loop; no keyboard event is suppressed.
            MainActor.assumeIsolated { owner.handle(type, event) }
            return Unmanaged.passUnretained(event)
        }
        guard let value = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap,
            options: .listenOnly, eventsOfInterest: mask, callback: callback,
            userInfo: Unmanaged.passUnretained(self).toOpaque()) else { return }
        tap = value
        tapSource = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, value, 0)
        if let source = tapSource { CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes) }
        CGEvent.tapEnable(tap: value, enable: true)
    }

    private func handle(_ type: CGEventType, _ event: CGEvent) {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            if let tap = tap { CGEvent.tapEnable(tap: tap, enable: true) }
            perform(gesture.otherKey())
            return
        }
        let code = event.getIntegerValueField(.keyboardEventKeycode)
        if type == .flagsChanged && code == 61 {
            // The device-dependent flag distinguishes right Option when left Option is also held.
            let down = event.flags.rawValue & 0x40 != 0
            let other = down && (0..<128).contains { key in
                key != 61 && CGEventSource.keyState(.combinedSessionState, key: CGKeyCode(key))
            }
            perform(gesture.rightOption(down: down, otherKeyDown: other, busy: busy,
                                        permitted: Permissions.missing.isEmpty))
        } else if type == .keyDown || type == .flagsChanged {
            perform(gesture.otherKey())
        }
    }

    private func perform(_ action: HoldGesture.Action) {
        switch action {
        case .none: break
        case .missingPermission: showMessage(Permissions.missing.first?.message ?? "Проверьте разрешения в меню значка.")
        case .start:
            lastAudio = nil; lastDuration = 0
            guard storageReady else {
                gesture.failedToStart(); showMessage(RecognitionStatus.failed.message); updateStatus(); return
            }
            do {
                try recorder.start()
                started = ProcessInfo.processInfo.systemUptime
                limitTimer = Timer.scheduledTimer(withTimeInterval: RecordingPolicy.maximumDuration, repeats: false) { [weak self] _ in
                    MainActor.assumeIsolated {
                        guard let self = self else { return }
                        self.perform(self.gesture.reachedLimit())
                    }
                }
                if let timer = limitTimer { RunLoop.main.add(timer, forMode: .common) }
            } catch { gesture.failedToStart(); showMessage(RecognitionStatus.failed.message) }
            updateStatus()
        case .discard:
            limitTimer?.invalidate(); limitTimer = nil
            recorder.discard(); started = nil; updateStatus()
        case .finish:
            guard let start = started else { return }
            let duration = min(RecordingPolicy.maximumDuration, ProcessInfo.processInfo.systemUptime - start)
            limitTimer?.invalidate(); limitTimer = nil; started = nil
            do {
                let audio = try recorder.stop()
                if RecordingPolicy.shouldSend(duration) {
                    lastAudio = audio; lastDuration = duration
                    send(audio, duration: duration)
                }
            } catch { showMessage(RecognitionStatus.failed.message) }
            updateStatus()
        }
    }

    private func send(_ audio: Data, duration: TimeInterval) {
        guard !busy else { return }
        busy = true; updateStatus()
        Task { @MainActor in
            let start = Date()
            let result: RecognitionResult
            let address = UserDefaults.standard.string(forKey: "server-address") ?? ""
            if address.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                result = RecognitionResult(.missingAddress)
            } else {
                do {
                    result = await client.transcribe(address: address, key: try ServerKey.read(),
                                                    audio: audio, duration: duration)
                } catch { result = RecognitionResult(.keyRejected) }
            }
            log(result.status, duration, Date().timeIntervalSince(start))
            if result.status == .ok, let text = result.text {
                if !paster.paste(text) { showMessage(Permissions.accessibility.message) }
            } else { showMessage(result.status.message) }
            busy = false; updateStatus()
        }
    }

    private func log(_ status: RecognitionStatus, _ duration: TimeInterval, _ elapsed: TimeInterval) {
        logger.info("outcome=\(status.rawValue, privacy: .public) duration=\(duration) response=\(elapsed)")
    }
    private func updateStatus() {
        let title = busy ? "Распознаю…" : started != nil ? "Запись" : ""
        let symbol = busy ? "ellipsis.circle" : started != nil ? "mic.fill" : "mic"
        statusItem.button?.image = NSImage(systemSymbolName: symbol, accessibilityDescription: title.isEmpty ? "Раскладушка" : title)
        statusItem.button?.title = title.isEmpty ? "" : " \(title)"
        retry.isEnabled = lastAudio != nil && !busy && started == nil
    }
    func menuWillOpen(_ menu: NSMenu) { refreshPermissions(); updateStatus() }
    private func refreshPermissions() {
        for item in permissionItems { menu.removeItem(item) }
        permissionItems = []
        for issue in Permissions.missing {
            let item = NSMenuItem(title: issue.message, action: #selector(openPermission(_:)), keyEquivalent: "")
            item.target = self; item.representedObject = issue.pane
            menu.insertItem(item, at: menu.numberOfItems - 1)
            permissionItems.append(item)
        }
    }
    @objc private func openPermission(_ item: NSMenuItem) {
        if let pane = item.representedObject as? String { Permissions.open(pane) }
    }
    @objc private func openSettings() { settings.show() }
    @objc private func retryLast() {
        guard !busy, started == nil, let audio = lastAudio else { return }
        send(audio, duration: lastDuration)
    }
    @objc private func quit() { NSApp.terminate(nil) }

    private func showMessage(_ message: String, seconds: TimeInterval = 4) {
        dismissPanel?.cancel(); panel?.orderOut(nil)
        let size = NSSize(width: 360, height: 94)
        let value = NSPanel(contentRect: NSRect(origin: .zero, size: size),
                            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        value.level = .statusBar
        value.hidesOnDeactivate = false
        value.isFloatingPanel = true
        value.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        value.backgroundColor = .windowBackgroundColor
        value.hasShadow = true
        let label = NSTextField(wrappingLabelWithString: message)
        label.frame = NSRect(x: 16, y: 12, width: 328, height: 70)
        value.contentView?.addSubview(label)
        if let button = statusItem.button, let anchor = button.window {
            let screen = anchor.screen ?? NSScreen.main
            let bounds = screen?.visibleFrame ?? anchor.frame
            let x = max(bounds.minX + 8, min(anchor.frame.midX - size.width / 2, bounds.maxX - size.width - 8))
            value.setFrameOrigin(NSPoint(x: x, y: bounds.maxY - size.height - 8))
        }
        panel = value
        value.orderFrontRegardless()
        let work = DispatchWorkItem { [weak self] in self?.panel?.orderOut(nil); self?.panel = nil }
        dismissPanel = work
        DispatchQueue.main.asyncAfter(deadline: .now() + seconds, execute: work)
    }
    func applicationWillTerminate(_ notification: Notification) {
        recorder.discard(); lastAudio = nil; paster.restore()
        limitTimer?.invalidate(); permissionTimer?.invalidate()
        if let tap = tap { CGEvent.tapEnable(tap: tap, enable: false); CFMachPortInvalidate(tap) }
    }
}

@main
struct RaskladushkaApplication {
    @MainActor static func main() {
        let application = NSApplication.shared
        let delegate = AppDelegate()
        application.delegate = delegate
        withExtendedLifetime(delegate) { application.run() }
    }
}
