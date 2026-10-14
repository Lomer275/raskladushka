using System.Runtime.InteropServices;
using Raskladushka.App.Native;
using Raskladushka.Core.Typing;

namespace Raskladushka.App.Input;

public sealed class InputInjector : IKeyOutput
{
    public static readonly nuint Tag = 0x4B425631;

    public void Replace(int deleteCount, string text)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(deleteCount);
        var inputs = new NativeMethods.INPUT[checked((deleteCount + text.Length) * 2)];
        var index = 0;
        for (var i = 0; i < deleteCount; i++)
        {
            inputs[index++] = Key(0x08, 0, 0);
            inputs[index++] = Key(0x08, 0, NativeMethods.KEYEVENTF_KEYUP);
        }
        foreach (var character in text)
        {
            inputs[index++] = Key(0, character, NativeMethods.KEYEVENTF_UNICODE);
            inputs[index++] = Key(0, character, NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP);
        }
        Send(inputs);
    }

    public void ReleaseModifierWithCover(int vk)
    {
        // A covered Alt release prevents menu activation before replacing text.
        var flags = NativeMethods.KEYEVENTF_KEYUP;
        if (vk == 0xA5) flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;
        Send([Key(0xE8, 0, 0), Key(0xE8, 0, NativeMethods.KEYEVENTF_KEYUP),
            Key(checked((ushort)vk), 0, flags)]);
    }

    public void SendChord(params int[] vks)
    {
        var inputs = new NativeMethods.INPUT[vks.Length * 2];
        for (var i = 0; i < vks.Length; i++)
        {
            var flags = vks[i] == 0x2D ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0u;
            inputs[i] = Key(checked((ushort)vks[i]), 0, flags);
            inputs[inputs.Length - 1 - i] = Key(checked((ushort)vks[i]), 0, flags | NativeMethods.KEYEVENTF_KEYUP);
        }
        Send(inputs);
    }

    public void ReleaseShift()
    {
        var inputs = new List<NativeMethods.INPUT>();
        foreach (var vk in new ushort[] { 0xA0, 0xA1 })
            if ((NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0)
                inputs.Add(Key(vk, 0, NativeMethods.KEYEVENTF_KEYUP));
        if (inputs.Count > 0) Send(inputs.ToArray());
    }

    internal static NativeMethods.INPUT Key(ushort vk, ushort scan, uint flags, nuint? tag = null) => new()
    {
        Type = 1,
        Data = new NativeMethods.INPUTUNION
        {
            Keyboard = new NativeMethods.KEYBDINPUT { Vk = vk, Scan = scan, Flags = flags, ExtraInfo = tag ?? Tag }
        }
    };

    internal static void Send(NativeMethods.INPUT[] inputs)
    {
        if (NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>()) != inputs.Length)
            throw new InvalidOperationException("Windows не приняла все события клавиатуры.");
    }
}
