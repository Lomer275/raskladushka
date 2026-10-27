import AppKit
import CoreGraphics
import ApplicationServices

@MainActor
final class TextPaster {
    private struct SavedItem { let values: [(NSPasteboard.PasteboardType, Data)] }
    private var saved: [SavedItem]?
    private var writtenCount: Int?
    private var restoration: DispatchWorkItem?

    func paste(_ text: String) -> Bool {
        guard AXIsProcessTrusted() else { return false }
        // Resolve an earlier transient paste before saving the next clipboard snapshot.
        restore()
        let board = NSPasteboard.general
        let before = board.changeCount
        let snapshot = (board.pasteboardItems ?? []).map { item in
            SavedItem(values: item.types.compactMap { type in item.data(forType: type).map { (type, $0) } })
        }
        guard board.changeCount == before,
              let down = CGEvent(keyboardEventSource: nil, virtualKey: 9, keyDown: true),
              let up = CGEvent(keyboardEventSource: nil, virtualKey: 9, keyDown: false) else { return false }
        let item = NSPasteboardItem()
        item.setString(text, forType: .string)
        item.setData(Data(), forType: NSPasteboard.PasteboardType("org.nspasteboard.TransientType"))
        item.setData(Data(), forType: NSPasteboard.PasteboardType("org.nspasteboard.ConcealedType"))
        board.clearContents()
        saved = snapshot
        let written = board.writeObjects([item])
        writtenCount = board.changeCount
        guard written else { restore(); return false }
        down.flags = .maskCommand
        up.flags = .maskCommand
        down.post(tap: .cghidEventTap)
        up.post(tap: .cghidEventTap)
        let work = DispatchWorkItem { [weak self] in self?.restore() }
        restoration = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5, execute: work)
        return true
    }

    func restore() {
        restoration?.cancel()
        restoration = nil
        defer { saved = nil; writtenCount = nil }
        let board = NSPasteboard.general
        guard let saved = saved, let count = writtenCount, board.changeCount == count else { return }
        let items = saved.map { entry -> NSPasteboardItem in
            let item = NSPasteboardItem()
            for (type, data) in entry.values { item.setData(data, forType: type) }
            return item
        }
        board.clearContents()
        if !items.isEmpty { board.writeObjects(items) }
    }
}
