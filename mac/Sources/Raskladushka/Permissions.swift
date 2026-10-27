import AppKit
import AVFoundation
import ApplicationServices

struct PermissionIssue {
    let message: String
    let pane: String
}

enum Permissions {
    static let accessibility = PermissionIssue(message: "Разрешите универсальный доступ", pane: "Privacy_Accessibility")

    static var missing: [PermissionIssue] {
        var issues: [PermissionIssue] = []
        if AVCaptureDevice.authorizationStatus(for: .audio) != .authorized {
            issues.append(PermissionIssue(message: "Разрешите доступ к микрофону", pane: "Privacy_Microphone"))
        }
        if !CGPreflightListenEventAccess() {
            issues.append(PermissionIssue(message: "Разрешите мониторинг ввода", pane: "Privacy_ListenEvent"))
        }
        if !AXIsProcessTrusted() {
            issues.append(accessibility)
        }
        return issues
    }
    static func request() {
        AVCaptureDevice.requestAccess(for: .audio) { _ in }
        _ = CGRequestListenEventAccess()
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        _ = AXIsProcessTrustedWithOptions(options)
    }
    static func open(_ pane: String) {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?\(pane)") {
            NSWorkspace.shared.open(url)
        }
    }
}
