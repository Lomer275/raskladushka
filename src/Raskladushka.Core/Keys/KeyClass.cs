namespace Raskladushka.Core.Keys;

public enum KeyKind { Char, Backspace, Reset, Shift, CapsLock, Modifier, Ignore }

public readonly record struct KeyClass(KeyKind Kind, PhysKey Key = default);
