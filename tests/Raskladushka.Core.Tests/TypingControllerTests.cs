using Raskladushka.Core.Keys;
using Raskladushka.Core.Lang;
using Raskladushka.Core.Typing;
using Microsoft.Extensions.Time.Testing;

namespace Raskladushka.Core.Tests;

public class TypingControllerTests
{
    [Fact]
    public void AutoSwitchSwallowsOnlyTerminator()
    {
        var h = new Harness();
        h.Type("ghbdtn");
        Assert.True(h.Key(0x20, 0x39));
        Assert.Equal([(6, "привет ")], h.Output.Calls);
        Assert.Equal([Layout.Ru], h.Layouts.Requests);
        Assert.Equal([TypingStat.AutoSwitched], h.Stats);
    }

    [Fact]
    public void CorrectWordIsKept()
    {
        var h = new Harness();
        h.Type("hello ");
        Assert.Empty(h.Output.Calls);
        Assert.Empty(h.Layouts.Requests);
        Assert.Empty(h.Stats);
    }

    [Fact]
    public void UndoRestoresOriginalAndAddsException()
    {
        var h = new Harness();
        h.AutoSwitch();
        Assert.True(h.Fix());
        Assert.Equal([(6, "привет "), (7, "ghbdtn ")], h.Output.Calls);
        Assert.Equal([Layout.Ru, Layout.En], h.Layouts.Requests);
        Assert.Equal(["ghbdtn"], h.Exceptions.Words);
        Assert.Equal([TypingStat.AutoSwitched, TypingStat.UndoApplied], h.Stats);
    }

    [Fact]
    public void NewCharacterCancelsUndo()
    {
        var h = new Harness();
        h.AutoSwitch();
        h.Type("v");
        Assert.True(h.Fix());
        Assert.Equal((1, "v"), h.Output.Calls[^1]);
        Assert.Equal(Layout.En, h.Layouts.Requests[^1]);
        Assert.Empty(h.Exceptions.Words);
        Assert.Equal(TypingStat.FixApplied, h.Stats[^1]);
    }

    [Fact]
    public void RepeatedFixPreservesBuffer()
    {
        var h = new Harness();
        h.Type("ghbdtn");
        Assert.True(h.Fix());
        Assert.True(h.Fix());
        Assert.Equal([(6, "привет"), (6, "ghbdtn")], h.Output.Calls);
        Assert.Equal([Layout.Ru, Layout.En], h.Layouts.Requests);
        Assert.Equal([TypingStat.FixApplied, TypingStat.FixApplied], h.Stats);
    }

    [Fact]
    public void DisabledAutoSwitchStillAllowsFixWithTrailingKeys()
    {
        var h = new Harness { Options = new(false, 0x13) };
        h.Type("ghbdtn ");
        Assert.Empty(h.Output.Calls);
        Assert.Equal(0, h.DetectorCalls);
        Assert.True(h.Fix());
        Assert.Equal([(7, "привет ")], h.Output.Calls);
        Assert.Equal([Layout.Ru], h.Layouts.Requests);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void ModifiedKeyResetsBuffer(bool ctrl, bool alt, bool win)
    {
        var h = new Harness();
        h.Type("gh");
        Assert.False(h.Key(0x41, 0x1E, new(false, ctrl, alt, win, false)));
        h.Type("bdtn ");
        Assert.Empty(h.Output.Calls);
    }

    [Fact]
    public void EnterResetsBuffer()
    {
        var h = new Harness();
        h.Type("ghbdtn");
        Assert.False(h.Key(0x0D));
        Assert.False(h.Fix());
        Assert.Empty(h.Output.Calls);
    }

    [Fact]
    public void UnknownLayoutPreventsReplacementButKeepsBuffer()
    {
        var h = new Harness();
        h.Layouts.Current = null;
        h.Type("ghbdtn ");
        Assert.False(h.Fix());
        Assert.Empty(h.Output.Calls);
        h.Layouts.Current = Layout.En;
        Assert.True(h.Fix());
        Assert.Equal([(7, "привет ")], h.Output.Calls);
    }

    [Fact]
    public void RussianLayoutSwitchesToEnglish()
    {
        var h = new Harness();
        h.Layouts.Current = Layout.Ru;
        h.Type("hello");
        Assert.True(h.Key(0x20, 0x39));
        Assert.Equal([(5, "hello ")], h.Output.Calls);
        Assert.Equal([Layout.En], h.Layouts.Requests);
    }

    [Fact]
    public void PendingLayoutProtectsNextWordAndExpires()
    {
        var h = new Harness();
        h.AutoSwitch();
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        h.Type("vbh ");
        Assert.Single(h.Output.Calls);
        h.Time.Advance(TimeSpan.FromMilliseconds(600));
        h.AutoSwitch();
        Assert.Equal([(6, "привет "), (6, "привет ")], h.Output.Calls);
    }

    [Fact]
    public void KeyUpsDoNotChangeState()
    {
        var h = new Harness();
        foreach (var c in "ghbdtn")
        {
            h.Type(c.ToString());
            Assert.False(h.Key(0x0D, down: false));
        }
        Assert.True(h.Key(0x20, 0x39));
        Assert.False(h.Key(0x41, 0x1E, new(false, true, false, false, false), false));
        Assert.False(h.Key(0x13, down: false));
        Assert.True(h.Fix());
        Assert.Equal([(6, "привет "), (7, "ghbdtn ")], h.Output.Calls);
        Assert.Single(h.Exceptions.Words);
    }

    [Fact]
    public void ShiftFixPreservesBufferAndUndo()
    {
        var h = new Harness();
        h.Type("ghb");
        Assert.False(h.Fix(new(true, false, false, false, false)));
        Assert.Empty(h.Output.Calls);
        h.Type("dtn");
        Assert.True(h.Key(0x20, 0x39));
        Assert.False(h.Fix(new(true, false, false, false, false)));
        Assert.True(h.Fix());
        Assert.Equal((7, "ghbdtn "), h.Output.Calls[^1]);
        Assert.Single(h.Exceptions.Words);
    }

    [Fact]
    public void CapsIsPreserved()
    {
        var h = new Harness();
        h.Type("ghbdtn", new(false, false, false, false, true));
        Assert.True(h.Key(0x20, 0x39));
        Assert.Equal([(6, "ПРИВЕТ ")], h.Output.Calls);
    }

    [Fact]
    public void ShiftIsPreserved()
    {
        var h = new Harness();
        h.Type("g", new(true, false, false, false, false));
        h.Type("hbdtn");
        Assert.True(h.Key(0x20, 0x39));
        Assert.Equal([(6, "Привет ")], h.Output.Calls);
    }

    [Fact]
    public void TerminatorIsRenderedInTargetLayout()
    {
        var h = new Harness();
        h.Type("ghbdtn");
        Assert.True(h.Key(0xBF, 0x35, new(true, false, false, false, false)));
        Assert.Equal([(6, "привет,")], h.Output.Calls);
    }

    [Fact]
    public void ExternalResetClearsPartialWord()
    {
        var h = new Harness();
        h.Type("ghb");
        h.Controller.ResetExternal();
        Assert.False(h.Fix());
        Assert.Empty(h.Output.Calls);
        h.Type("dtn");
        Assert.True(h.Key(0x20, 0x39));
        Assert.Equal([(3, "вет ")], h.Output.Calls);
    }

    [Fact]
    public void ExternalResetClearsUndoAndPendingLayout()
    {
        var h = new Harness();
        h.AutoSwitch();
        h.Controller.ResetExternal();
        Assert.False(h.Fix());
        h.AutoSwitch();
        Assert.Equal(2, h.Output.Calls.Count);
        Assert.Empty(h.Exceptions.Words);
    }

    [Theory]
    [InlineData(0x10)]
    [InlineData(0x14)]
    [InlineData(0x70)]
    [InlineData(0x11)]
    public void PassiveKeyClassesPreserveUndo(int vk)
    {
        var h = new Harness();
        h.AutoSwitch();
        Assert.False(h.Key(vk));
        Assert.False(h.Key(vk, down: false));
        Assert.True(h.Fix());
        Assert.Single(h.Exceptions.Words);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void ModifiedFixClearsUndoAndBuffer(bool ctrl, bool alt, bool win)
    {
        var h = new Harness();
        h.AutoSwitch();
        Assert.False(h.Fix(new(false, ctrl, alt, win, false)));
        Assert.False(h.Fix());
        Assert.Single(h.Output.Calls);
        Assert.Empty(h.Exceptions.Words);
    }

    [Fact]
    public void BackspaceEditsBufferAndCancelsUndo()
    {
        var h = new Harness();
        h.AutoSwitch();
        Assert.False(h.Key(0x08));
        Assert.True(h.Fix());
        Assert.Equal((6, "ghbdtn"), h.Output.Calls[^1]);
        Assert.Empty(h.Exceptions.Words);
    }

    [Fact]
    public void ExtraTerminatorCancelsUndoAndIsIncludedInFix()
    {
        var h = new Harness();
        h.AutoSwitch();
        h.Type(" ");
        Assert.True(h.Fix());
        Assert.Equal((8, "ghbdtn  "), h.Output.Calls[^1]);
        Assert.Empty(h.Exceptions.Words);
    }

    [Fact]
    public void ConfirmedLayoutNoLongerOverridesExternalChange()
    {
        var h = new Harness();
        h.AutoSwitch();
        h.Layouts.Current = Layout.Ru;
        h.Type("v");
        h.Layouts.Current = Layout.En;
        Assert.True(h.Fix());
        Assert.Equal((1, "м"), h.Output.Calls[^1]);
    }

    [Fact]
    public void UnknownLayoutOverridesPendingRequest()
    {
        var h = new Harness();
        h.AutoSwitch();
        h.Layouts.Current = null;
        Assert.False(h.Fix());
        Assert.Single(h.Output.Calls);
    }

    [Fact]
    public void OptionsAreReadOnEveryEvent()
    {
        var h = new Harness { Options = new(false, 0x13) };
        h.Type("ghbdtn");
        h.Options = new(true, 0x91);
        Assert.True(h.Key(0x20, 0x39));
        Assert.False(h.Fix());
        Assert.True(h.Key(0x91));
        Assert.False(h.Key(0x91, down: false));
        Assert.Equal(10, h.OptionsCalls);
        Assert.Single(h.Exceptions.Words);
    }

    [Fact]
    public void UndoTrimsAndLowercasesException()
    {
        var h = new Harness();
        h.Type(";BPYM");
        Assert.True(h.Key(0x20, 0x39));
        Assert.True(h.Fix());
        Assert.Equal(["bpym"], h.Exceptions.Words);
    }

    [Fact]
    public async Task StatIsRaisedOutsideStateLock()
    {
        var h = new Harness();
        Task? reset = null;
        var completed = false;
        h.Controller.Stat += _ =>
        {
            reset = Task.Run(h.Controller.ResetExternal);
            completed = reset.Wait(TimeSpan.FromSeconds(5));
        };
        h.AutoSwitch();
        Assert.NotNull(reset);
        await reset;
        Assert.True(completed);
        Assert.False(h.Fix());
    }

    [Fact]
    public void AutoAndUndoEventsIncludeWordsDirectionAndOriginalReason()
    {
        var h = new Harness();
        var events = new List<TypingEvent>();
        h.Controller.Event += events.Add;
        h.AutoSwitch();
        Assert.Equal(1, h.DetectorCalls);
        Assert.True(h.Fix());
        Assert.Equal(1, h.DetectorCalls);
        Assert.Equal([
            new TypingEvent(TypingStat.AutoSwitched, Layout.En, Layout.Ru, 6,
                DecisionReason.AltMoreFrequent, "ghbdtn", "привет"),
            new TypingEvent(TypingStat.UndoApplied, Layout.Ru, Layout.En, 6,
                DecisionReason.AltMoreFrequent, "привет", "ghbdtn")], events);
        Assert.Equal([TypingStat.AutoSwitched, TypingStat.UndoApplied], h.Stats);
    }

    [Fact]
    public void FixEventExplainsKeptWordAndExcludesTrailingKeys()
    {
        var h = new Harness();
        var events = new List<TypingEvent>();
        h.Controller.Event += events.Add;
        h.Type("hello  ");
        Assert.Empty(events);
        Assert.True(h.Fix());
        Assert.Equal(new TypingEvent(TypingStat.FixApplied, Layout.En, Layout.Ru, 5,
            DecisionReason.CurInDictionary, "hello", "руддщ"), Assert.Single(events));
        Assert.Equal([TypingStat.FixApplied], h.Stats);
    }

    [Fact]
    public async Task DetailedEventIsRaisedOutsideStateLock()
    {
        var h = new Harness();
        Task? reset = null;
        var completed = false;
        h.Controller.Event += _ =>
        {
            reset = Task.Run(h.Controller.ResetExternal);
            completed = reset.Wait(TimeSpan.FromSeconds(5));
        };
        h.AutoSwitch();
        Assert.NotNull(reset);
        await reset;
        Assert.True(completed);
    }

    [Theory]
    [InlineData(0x12)]
    [InlineData(0xA4)]
    [InlineData(0xA5)]
    public void AltTapPreservesWordAndReleasesBeforeReplacing(int vk)
    {
        var h = new Harness { Options = new(false, 0x13, true) };
        var events = new List<TypingEvent>();
        h.Controller.Event += events.Add;
        h.Type("ghbdtn");
        Assert.False(h.Key(vk, mods: new(false, false, true, false, false)));
        Assert.Empty(h.Output.Operations);
        Assert.False(h.Key(0x4E, down: false));
        Assert.True(h.Key(vk, down: false));
        Assert.Equal([$"release:{vk}", "replace"], h.Output.Operations);
        Assert.Equal([(6, "привет")], h.Output.Calls);
        Assert.Equal([Layout.Ru], h.Layouts.Requests);
        Assert.Equal([TypingStat.FixApplied], h.Stats);
        Assert.Equal(TypingStat.FixApplied, Assert.Single(events).Kind);
        Assert.Empty(h.Skipped);
        Assert.False(h.Key(vk, down: false));
    }

    [Fact]
    public void AltTapUndoesAutoSwitchAndAddsException()
    {
        var h = new Harness { Options = new(true, 0x13, true) };
        var events = new List<TypingEvent>();
        h.Controller.Event += events.Add;
        h.AutoSwitch();
        Assert.False(h.Key(0xA4, mods: new(false, false, true, false, false)));
        Assert.True(h.Key(0xA4, down: false));
        Assert.Equal(["replace", "release:164", "replace"], h.Output.Operations);
        Assert.Equal([(6, "привет "), (7, "ghbdtn ")], h.Output.Calls);
        Assert.Equal([Layout.Ru, Layout.En], h.Layouts.Requests);
        Assert.Equal(["ghbdtn"], h.Exceptions.Words);
        Assert.Equal([TypingStat.AutoSwitched, TypingStat.UndoApplied], h.Stats);
        Assert.Equal(TypingStat.UndoApplied, events[^1].Kind);
        Assert.Empty(h.Skipped);
    }

    [Theory]
    [InlineData(0x41, 0x1E)]
    [InlineData(0x09, 0)]
    [InlineData(0xA0, 0)]
    [InlineData(0xA2, 0)]
    [InlineData(0x5B, 0)]
    [InlineData(0x73, 0)]
    public void OtherKeyDownCancelsAltTap(int vk, int scan)
    {
        var h = new Harness { Options = new(false, 0x13, true) };
        h.Type("ghbdtn");
        Assert.False(h.Key(0xA4, mods: new(false, false, true, false, false)));
        Assert.False(h.Key(vk, scan, new(vk == 0xA0, vk == 0xA2, true, vk == 0x5B, false)));
        Assert.False(h.Key(0xA4, down: false));
        Assert.Empty(h.Output.Operations);
        Assert.Empty(h.Skipped);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void ModifiedAltDoesNotArm(bool shift, bool ctrl, bool win)
    {
        var h = new Harness { Options = new(false, 0x13, true) };
        h.Type("ghbdtn");
        Assert.False(h.Key(0xA4, mods: new(shift, ctrl, true, win, false)));
        Assert.False(h.Key(0xA4, down: false));
        Assert.Empty(h.Output.Operations);
        Assert.Empty(h.Skipped);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RepeatOrSecondAltCancelsTap(bool repeat)
    {
        var h = new Harness { Options = new(false, 0x13, true) };
        h.Type("ghbdtn");
        var mods = new Modifiers(false, false, true, false, false);
        Assert.False(h.Key(0xA4, mods: mods));
        Assert.False(h.Key(repeat ? 0xA4 : 0xA5, mods: mods, repeat: repeat));
        Assert.False(h.Key(0xA4, down: false));
        if (repeat)
            Assert.Equal(new TapDiagnostic(TapSkipReason.NotArmed, TapDisarm.Repeat, BufferReset.AltPress, 0, false),
                Assert.Single(h.Skipped));
        else Assert.Empty(h.Skipped);
        Assert.False(h.Key(0xA5, down: false));
        Assert.Equal(TapDisarm.None, h.Skipped[^1].Disarm);
        Assert.Empty(h.Output.Operations);
    }

    [Fact]
    public void ExternalResetCancelsAltTap()
    {
        var h = new Harness { Options = new(false, 0x13, true) };
        h.Type("ghbdtn");
        h.Key(0xA4, mods: new(false, false, true, false, false));
        h.Controller.ResetExternal();
        Assert.False(h.Key(0xA4, down: false));
        Assert.Empty(h.Output.Operations);
        Assert.Equal(new TapDiagnostic(TapSkipReason.NotArmed, TapDisarm.Reset, BufferReset.External, 0, false),
            Assert.Single(h.Skipped));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EmptyBufferOrUnknownLayoutDoesNotReleaseAlt(bool unknownLayout, bool undo)
    {
        var h = new Harness { Options = new(true, 0x13, true) };
        if (undo) h.AutoSwitch();
        else if (unknownLayout) h.Type("ghbdtn");
        h.Output.Operations.Clear();
        h.Output.Calls.Clear();
        if (unknownLayout) h.Layouts.Current = null;
        h.Key(0xA4, mods: new(false, false, true, false, false));
        Assert.False(h.Key(0xA4, down: false));
        Assert.Empty(h.Output.Operations);
        Assert.Empty(h.Output.Calls);
        Assert.Equal(new TapDiagnostic(unknownLayout ? TapSkipReason.LayoutUnknown : TapSkipReason.NoTarget,
            TapDisarm.None, BufferReset.None, null, false), Assert.Single(h.Skipped));
    }

    [Fact]
    public void FailedModifierReleaseDoesNotReplaceOrSwallow()
    {
        var h = new Harness { Options = new(false, 0x13, true) };
        h.Type("ghbdtn");
        h.Output.FailRelease = true;
        h.Key(0xA4, mods: new(false, false, true, false, false));
        Assert.False(h.Key(0xA4, down: false));
        Assert.Equal(["release:164"], h.Output.Operations);
        Assert.Empty(h.Output.Calls);
        Assert.Empty(h.Layouts.Requests);
        Assert.Empty(h.Stats);
        Assert.Equal(TapSkipReason.ReleaseFailed, Assert.Single(h.Skipped).Reason);
        Assert.False(h.Key(0xA4, down: false));
    }

    [Fact]
    public void DisabledAltTapRetainsLegacyReset()
    {
        var h = new Harness();
        h.Type("ghbdtn");
        Assert.False(h.Key(0xA4, mods: new(false, false, true, false, false)));
        Assert.False(h.Key(0xA4, down: false));
        Assert.False(h.Fix());
        Assert.Empty(h.Output.Operations);
        Assert.Empty(h.Skipped);
    }

    [Fact]
    public void AltTapModeIgnoresFixHotkeyVk()
    {
        var h = new Harness { Options = new(false, 0x13, true) };
        h.Type("ghbdtn");
        Assert.False(h.Fix());
        Assert.Empty(h.Output.Operations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResetBeforeTapReportsCauseAndAge(bool external)
    {
        var h = new Harness { Options = new(false, 0x13, true) };
        h.Type("ghbdtn");
        if (external) h.Controller.ResetExternal();
        else h.Key(0x0D);
        h.Time.Advance(TimeSpan.FromMilliseconds(123));
        h.Key(0xA4, mods: new(false, false, true, false, false));
        h.Time.Advance(TimeSpan.FromMilliseconds(45));
        Assert.False(h.Key(0xA4, down: false));
        Assert.Equal(new TapDiagnostic(TapSkipReason.NoTarget, TapDisarm.None,
            external ? BufferReset.External : BufferReset.Key, 168, false), Assert.Single(h.Skipped));
        Assert.Empty(h.Output.Operations);
    }

    [Fact]
    public void AltUpWithoutDownReportsNotArmed()
    {
        var h = new Harness { Options = new(false, 0x13, true) };
        Assert.False(h.Key(0xA4, down: false));
        Assert.Equal(new TapDiagnostic(TapSkipReason.NotArmed, TapDisarm.None, BufferReset.None, null, false),
            Assert.Single(h.Skipped));
    }

    [Fact]
    public void RepeatWithoutArmingPreservesDisarmReason()
    {
        var h = new Harness { Options = new(false, 0x13, true) };
        h.Key(0xA4);
        h.Controller.ResetExternal();
        h.Key(0xA4, repeat: true);
        h.Key(0xA4, down: false);
        Assert.Equal(new TapDiagnostic(TapSkipReason.NotArmed, TapDisarm.Reset, BufferReset.AltPress, 0, false),
            Assert.Single(h.Skipped));
    }

    [Fact]
    public async Task TapSkippedIsRaisedOutsideStateLock()
    {
        var h = new Harness { Options = new(false, 0x13, true) };
        Task? reset = null;
        var completed = false;
        h.Controller.TapSkipped += _ =>
        {
            reset = Task.Run(h.Controller.ResetExternal);
            completed = reset.Wait(TimeSpan.FromSeconds(5));
        };
        h.Key(0xA4);
        h.Key(0xA4, down: false);
        Assert.NotNull(reset);
        await reset;
        Assert.True(completed);
    }

    [Theory]
    [InlineData(0x12, false)]
    [InlineData(0xA4, false)]
    [InlineData(0xA5, false)]
    [InlineData(0xA4, true)]
    public void AltTapConvertsSelectionWithoutTarget(int vk, bool unknownLayout)
    {
        var h = new Harness { Options = new(false, 0x13, true) };
        var events = new List<TypingEvent>();
        h.Controller.Event += events.Add;
        if (unknownLayout) h.Layouts.Current = null;
        Assert.False(h.Controller.OnKey(new(vk, 0, false, true),
            new(false, false, true, false, false), true, out var onDown));
        Assert.False(onDown);
        Assert.True(h.Controller.OnKey(new(vk, 0, false, false), default, true, out var convert));
        Assert.True(convert);
        Assert.Equal([$"release:{vk}"], h.Output.Operations);
        Assert.Empty(h.Output.Calls);
        Assert.Empty(h.Skipped);
        Assert.Empty(h.Stats);
        Assert.Empty(events);
    }

    [Fact]
    public void AltTapWithoutSelectionReportsNoTarget()
    {
        var h = new Harness { Options = new(false, 0x13, true) };
        h.Key(0xA4);
        Assert.False(h.Controller.OnKey(new(0xA4, 0, false, false), default, false, out var convert));
        Assert.False(convert);
        var diagnostic = Assert.Single(h.Skipped);
        Assert.Equal(TapSkipReason.NoTarget, diagnostic.Reason);
        Assert.False(diagnostic.SelectionLikely);
        Assert.Empty(h.Output.Operations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WordAndUndoTakePriorityOverSelection(bool undo)
    {
        var h = new Harness { Options = new(true, 0x13, true) };
        if (undo) h.AutoSwitch();
        else h.Type("ghbdtn");
        h.Key(0xA4);
        Assert.True(h.Controller.OnKey(new(0xA4, 0, false, false), default, true, out var convert));
        Assert.False(convert);
        Assert.Equal(undo ? TypingStat.UndoApplied : TypingStat.FixApplied, h.Stats[^1]);
        Assert.Equal(undo ? (7, "ghbdtn ") : (6, "привет"), h.Output.Calls[^1]);
        Assert.Empty(h.Skipped);
    }

    [Theory]
    [InlineData(0x09)]
    [InlineData(0xA0)]
    public void AltChordDoesNotConvertSelection(int vk)
    {
        var h = new Harness { Options = new(false, 0x13, true) };
        h.Key(0xA4);
        Assert.False(h.Controller.OnKey(new(vk, 0, false, true),
            new(vk == 0xA0, false, true, false, false), true, out var onChord));
        Assert.False(onChord);
        Assert.False(h.Controller.OnKey(new(0xA4, 0, false, false), default, true, out var convert));
        Assert.False(convert);
        Assert.Empty(h.Output.Operations);
        Assert.Empty(h.Skipped);
    }

    [Fact]
    public void ExternalResetDuringAltDoesNotConvertSelection()
    {
        var h = new Harness { Options = new(false, 0x13, true) };
        h.Key(0xA4);
        h.Controller.ResetExternal();
        Assert.False(h.Controller.OnKey(new(0xA4, 0, false, false), default, true, out var convert));
        Assert.False(convert);
        Assert.Empty(h.Output.Operations);
        Assert.Equal(new TapDiagnostic(TapSkipReason.NotArmed, TapDisarm.Reset,
            BufferReset.External, 0, true), Assert.Single(h.Skipped));
    }

    [Fact]
    public void FailedSelectionModifierReleaseDoesNotSwallow()
    {
        var h = new Harness { Options = new(false, 0x13, true) };
        h.Output.FailRelease = true;
        h.Key(0xA4);
        Assert.False(h.Controller.OnKey(new(0xA4, 0, false, false), default, true, out var convert));
        Assert.False(convert);
        Assert.Equal(["release:164"], h.Output.Operations);
        Assert.Empty(h.Output.Calls);
        Assert.Empty(h.Stats);
        Assert.Equal(TapSkipReason.ReleaseFailed, Assert.Single(h.Skipped).Reason);
        Assert.True(h.Skipped[0].SelectionLikely);
    }

    [Fact]
    public void LegacyModeNeverRequestsSelectionConversion()
    {
        var h = new Harness();
        h.Type("ghbdtn");
        foreach (var vk in new[] { 0x13, 0xA4, 0xA5, 0x12, 0x41 })
        foreach (var down in new[] { true, false })
        {
            h.Controller.OnKey(new(vk, 0, false, down), default, true, out var convert);
            Assert.False(convert);
        }
        Assert.Empty(h.Skipped);
    }

    private sealed class Harness
    {
        public RecordingOutput Output { get; } = new();
        public RecordingLayouts Layouts { get; } = new();
        public RecordingExceptions Exceptions { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public TypingOptions Options { get; set; } = new(true, 0x13);
        public List<TypingStat> Stats { get; } = [];
        public List<TapDiagnostic> Skipped { get; } = [];
        public int DetectorCalls { get; private set; }
        public int OptionsCalls { get; private set; }
        public TypingController Controller { get; }

        public Harness()
        {
            var detector = new LayoutDetector(LangData.Load(Layout.En), LangData.Load(Layout.Ru),
                _ => { DetectorCalls++; return false; }, () => new DetectorOptions());
            Controller = new(detector, Exceptions, Output, Layouts,
                () => { OptionsCalls++; return Options; }, Time);
            Controller.Stat += Stats.Add;
            Controller.TapSkipped += Skipped.Add;
        }

        public bool Key(int vk, int scan = 0, Modifiers mods = default, bool down = true, bool repeat = false) =>
            Controller.OnKey(new(vk, scan, false, down, repeat), mods);

        public bool Fix(Modifiers mods = default) => Key(0x13, mods: mods);

        public void Type(string text, Modifiers mods = default)
        {
            foreach (var c in text)
            {
                Assert.True(KeyMap.TryFromChar(c, Layout.En, out var stroke));
                var scan = Enumerable.Range(0, 0x40).Single(s =>
                    KeyClassifier.Classify(0, s, false) == new KeyClass(KeyKind.Char, stroke.Key));
                Assert.False(Key(stroke.Key == PhysKey.Space ? 0x20 : 0, scan,
                    mods with { Shift = mods.Shift || stroke.Shift }));
            }
        }

        public void AutoSwitch()
        {
            Type("ghbdtn");
            Assert.True(Key(0x20, 0x39));
        }
    }

    private sealed class RecordingOutput : IKeyOutput
    {
        public List<(int DeleteCount, string Text)> Calls { get; } = [];
        public List<string> Operations { get; } = [];
        public bool FailRelease { get; set; }
        public void ReleaseModifierWithCover(int vk)
        {
            Operations.Add($"release:{vk}");
            if (FailRelease) throw new InvalidOperationException();
        }
        public void Replace(int deleteCount, string text)
        {
            Operations.Add("replace");
            Calls.Add((deleteCount, text));
        }
    }

    private sealed class RecordingLayouts : ILayoutApi
    {
        public Layout? Current { get; set; } = Layout.En;
        public List<Layout> Requests { get; } = [];
        public Layout? GetCurrent() => Current;
        public void Request(Layout target) => Requests.Add(target);
    }

    private sealed class RecordingExceptions : IExceptionSink
    {
        public List<string> Words { get; } = [];
        public void Add(string lowerWord) => Words.Add(lowerWord);
    }
}
