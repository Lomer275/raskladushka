namespace Raskladushka.Core.Keys;

public readonly record struct KeyStroke(PhysKey Key, bool Shift = false, bool Caps = false);
