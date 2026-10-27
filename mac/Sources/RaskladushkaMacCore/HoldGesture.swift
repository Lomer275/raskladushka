import Foundation

public struct HoldGesture {
    public enum Action: Equatable { case none, start, discard, finish, missingPermission }
    private var held = false
    private var recording = false
    public init() {}
    public mutating func rightOption(down: Bool, otherKeyDown: Bool, busy: Bool, permitted: Bool) -> Action {
        if down {
            guard !held else { return .none }
            held = true
            guard !busy, !otherKeyDown else { return .none }
            guard permitted else { return .missingPermission }
            recording = true
            return .start
        }
        held = false
        guard recording else { return .none }
        recording = false
        return .finish
    }
    public mutating func otherKey() -> Action {
        guard recording else { return .none }
        recording = false
        return .discard
    }
    public mutating func reachedLimit() -> Action {
        guard recording else { return .none }
        recording = false
        return .finish
    }
    public mutating func failedToStart() { recording = false }
}
