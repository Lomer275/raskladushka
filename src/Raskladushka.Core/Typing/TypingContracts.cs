using Raskladushka.Core.Keys;

namespace Raskladushka.Core.Typing;

public readonly record struct KeyEvent(int Vk, int Scan, bool Extended, bool IsDown, bool Repeat = false);
public readonly record struct Modifiers(bool Shift, bool Ctrl, bool Alt, bool Win, bool Caps);
public enum MouseButton { Left, Right, Middle, X }
public readonly record struct MouseInput(MouseButton Button, bool IsDown, int X, int Y, long TimeMs, bool Shift);
public readonly record struct SelectionHintOptions(int DragX, int DragY, int DoubleClickX, int DoubleClickY, int DoubleClickMs);

public enum CopyWaitStatus { Copied, NothingCopied, Busy }
public readonly record struct CopyWaitOptions(int FirstMs, int QuietMs, int SettleCapMs, int PollMs);
public readonly record struct CopyWaitResult(CopyWaitStatus Status, long FirstMs, long LastMs, int Changes, long ElapsedMs);

public interface IKeyOutput { void Replace(int deleteCount, string text); void ReleaseModifierWithCover(int vk); }
public interface ILayoutApi { Layout? GetCurrent(); void Request(Layout target); }
public interface IExceptionSink { void Add(string lowerWord); }

public sealed record TypingOptions(bool AutoSwitch, int FixHotkeyVk, bool FixOnAltTap = false);
public enum TypingStat { AutoSwitched, FixApplied, UndoApplied }

public sealed record TypingEvent(TypingStat Kind, Layout From, Layout To, int Length, DecisionReason Reason, string Before, string After);

public enum TapSkipReason { NotArmed, NoTarget, LayoutUnknown, ReleaseFailed }
public enum TapDisarm { None, Repeat, Modifiers, SecondAlt, OtherKey, Reset }
public enum BufferReset { None, External, Key, Chord, AltPress }
public readonly record struct TapDiagnostic(TapSkipReason Reason, TapDisarm Disarm, BufferReset LastReset, long? LastResetAgeMs, bool SelectionLikely);
