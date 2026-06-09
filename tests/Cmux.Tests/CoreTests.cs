using System.Reflection;
using System.Text;
using System.Text.Json;
using Cmux.Core.Models;
using Cmux.Core.Services;
using Cmux.Core.Terminal;
using FluentAssertions;
using Xunit;

namespace Cmux.Tests;

public class VtParserTests
{
    [Fact]
    public void Feed_PrintableCharacters_RaisesOnPrint()
    {
        var parser = new VtParser();
        var printed = new List<char>();
        parser.OnPrint = c => printed.Add(c);

        parser.Feed("Hello");

        printed.Should().Equal('H', 'e', 'l', 'l', 'o');
    }

    [Fact]
    public void Feed_C0Controls_RaisesOnExecute()
    {
        var parser = new VtParser();
        var executed = new List<byte>();
        parser.OnExecute = b => executed.Add(b);

        parser.Feed("\r\n");

        executed.Should().Contain(0x0D); // CR
        executed.Should().Contain(0x0A); // LF
    }

    [Fact]
    public void Feed_CsiSequence_RaisesOnCsiDispatch()
    {
        var parser = new VtParser();
        List<int>? receivedParams = null;
        char receivedFinal = '\0';
        parser.OnCsiDispatch = (parameters, final, qualifier) =>
        {
            receivedParams = new List<int>(parameters);
            receivedFinal = final;
        };

        // CSI 10;20H = cursor position (row 10, col 20)
        parser.Feed("\x1b[10;20H");

        receivedFinal.Should().Be('H');
        receivedParams.Should().NotBeNull();
        receivedParams.Should().Equal(10, 20);
    }

    [Fact]
    public void Feed_SgrReset_RaisesOnCsiDispatch()
    {
        var parser = new VtParser();
        char receivedFinal = '\0';
        parser.OnCsiDispatch = (parameters, final, qualifier) =>
        {
            receivedFinal = final;
        };

        parser.Feed("\x1b[0m");

        receivedFinal.Should().Be('m');
    }

    [Fact]
    public void Feed_OscString_RaisesOnOscDispatch()
    {
        var parser = new VtParser();
        string? receivedOsc = null;
        parser.OnOscDispatch = osc => receivedOsc = osc;

        // OSC 0 ; My Title BEL
        parser.Feed("\x1b]0;My Title\x07");

        receivedOsc.Should().Be("0;My Title");
    }

    [Fact]
    public void Feed_Osc9Notification_Detected()
    {
        var parser = new VtParser();
        string? receivedOsc = null;
        parser.OnOscDispatch = osc => receivedOsc = osc;

        parser.Feed("\x1b]9;Agent needs input\x07");

        receivedOsc.Should().Be("9;Agent needs input");
    }

    [Fact]
    public void Feed_Osc777Notification_Detected()
    {
        var parser = new VtParser();
        string? receivedOsc = null;
        parser.OnOscDispatch = osc => receivedOsc = osc;

        parser.Feed("\x1b]777;notify;Claude;Waiting for input\x07");

        receivedOsc.Should().Be("777;notify;Claude;Waiting for input");
    }

    [Fact]
    public void Feed_EscSequence_RaisesOnEscDispatch()
    {
        var parser = new VtParser();
        byte? dispatched = null;
        parser.OnEscDispatch = b => dispatched = b;

        // ESC 7 = DECSC (save cursor)
        parser.Feed("\u001b7");

        dispatched.Should().Be((byte)'7');
    }

    [Fact]
    public void Feed_PrivateModeSet_ParsesCorrectly()
    {
        var parser = new VtParser();
        string? receivedQualifier = null;
        List<int>? receivedParams = null;
        parser.OnCsiDispatch = (parameters, final, qualifier) =>
        {
            receivedParams = new List<int>(parameters);
            receivedQualifier = qualifier;
        };

        // CSI ? 25 h = show cursor (DECTCEM)
        parser.Feed("\x1b[?25h");

        receivedParams.Should().Equal(25);
        receivedQualifier.Should().Contain("?");
    }
}

public class TerminalBufferTests
{
    [Fact]
    public void WriteChar_AdvancesCursor()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.WriteChar('A');

        buffer.CursorCol.Should().Be(1);
        buffer.CellAt(0, 0).Character.Should().Be('A');
    }

    [Fact]
    public void LineFeed_AtBottom_ScrollsUp()
    {
        var buffer = new TerminalBuffer(80, 3);

        buffer.WriteString("Line1");
        buffer.NewLine();
        buffer.WriteString("Line2");
        buffer.NewLine();
        buffer.WriteString("Line3");
        buffer.NewLine(); // Should scroll

        buffer.ScrollbackCount.Should().Be(1);
    }

    [Fact]
    public void EraseInDisplay_Mode2_ClearsAll()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.WriteString("Hello World");

        buffer.EraseInDisplay(2);

        buffer.CellAt(0, 0).Character.Should().Be(' ');
    }

    [Fact]
    public void Resize_PreservesContent()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.WriteString("ABC");

        buffer.Resize(40, 12);

        buffer.CellAt(0, 0).Character.Should().Be('A');
        buffer.CellAt(0, 1).Character.Should().Be('B');
        buffer.CellAt(0, 2).Character.Should().Be('C');
        buffer.Cols.Should().Be(40);
        buffer.Rows.Should().Be(12);
    }

    [Fact]
    public void ScrollRegion_ScrollsOnlyWithinRegion()
    {
        var buffer = new TerminalBuffer(10, 5);
        buffer.SetScrollRegion(1, 3);
        buffer.MoveCursorTo(3, 0); // Bottom of scroll region
        buffer.WriteString("X");
        buffer.LineFeed(); // Should scroll only lines 1-3

        buffer.CellAt(0, 0).Character.Should().Be(' '); // Line 0 untouched
    }

    [Fact]
    public void SaveRestore_CursorPosition()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.MoveCursorTo(5, 10);
        buffer.SaveCursor();

        buffer.MoveCursorTo(0, 0);
        buffer.RestoreCursor();

        buffer.CursorRow.Should().Be(5);
        buffer.CursorCol.Should().Be(10);
    }

    [Fact]
    public void WriteChar_WideCharacter_OccupiesTwoCells()
    {
        var buffer = new TerminalBuffer(4, 1);

        buffer.WriteChar('\u4E2D');
        buffer.WriteChar('A');

        buffer.CursorCol.Should().Be(3);
        buffer.CellAt(0, 0).Character.Should().Be('\u4E2D');
        buffer.CellAt(0, 0).Width.Should().Be(2);
        buffer.CellAt(0, 1).Width.Should().Be(0);
        buffer.CellAt(0, 2).Character.Should().Be('A');
        buffer.ExportPlainText().Should().Be("\u4E2DA");
    }

    [Fact]
    public void WriteChar_WideCharacterAtLastColumn_WrapsBeforeWriting()
    {
        var buffer = new TerminalBuffer(3, 2);
        buffer.WriteString("AB");

        buffer.WriteChar('\u4E2D');

        buffer.CellAt(0, 0).Character.Should().Be('A');
        buffer.CellAt(0, 1).Character.Should().Be('B');
        buffer.CellAt(1, 0).Character.Should().Be('\u4E2D');
        buffer.CellAt(1, 1).Width.Should().Be(0);
        buffer.CursorRow.Should().Be(1);
        buffer.CursorCol.Should().Be(2);
    }

    [Fact]
    public void EraseChars_OnWideContinuation_ClearsWholeCharacter()
    {
        var buffer = new TerminalBuffer(4, 1);
        buffer.WriteChar('\u4E2D');
        buffer.MoveCursorTo(0, 1);

        buffer.EraseChars(1);

        buffer.CellAt(0, 0).Character.Should().Be(' ');
        buffer.CellAt(0, 0).Width.Should().Be(1);
        buffer.CellAt(0, 1).Character.Should().Be(' ');
        buffer.CellAt(0, 1).Width.Should().Be(1);
    }

    [Fact]
    public void Resize_Wider_ReflowsSoftWrappedLine()
    {
        var buffer = new TerminalBuffer(5, 3);
        buffer.WriteString("abcdefghij");

        buffer.Resize(10, 3);

        GetScreenLine(buffer, 0).Should().StartWith("abcdefghij");
        buffer.CursorRow.Should().Be(0);
        buffer.CursorCol.Should().Be(9);
    }

    [Fact]
    public void Resize_Narrower_ReflowsLongLine()
    {
        var buffer = new TerminalBuffer(10, 4);
        buffer.WriteString("abcdef");

        buffer.Resize(3, 4);

        GetScreenLine(buffer, 0).Should().Be("abc");
        GetScreenLine(buffer, 1).Should().Be("def");
    }

    [Fact]
    public void Resize_Wider_PreservesHardLineBreaks()
    {
        var buffer = new TerminalBuffer(5, 3);
        buffer.WriteString("abc");
        buffer.NewLine();
        buffer.WriteString("def");

        buffer.Resize(10, 3);

        GetScreenLine(buffer, 0).Should().StartWith("abc");
        GetScreenLine(buffer, 1).Should().StartWith("def");
    }

    [Fact]
    public void Resize_Wider_ReflowsWideCells()
    {
        var buffer = new TerminalBuffer(4, 3);
        buffer.WriteString("\u4E2DAB\u4E2D");

        buffer.Resize(8, 3);

        buffer.CellAt(0, 0).Character.Should().Be('\u4E2D');
        buffer.CellAt(0, 0).Width.Should().Be(2);
        buffer.CellAt(0, 1).Width.Should().Be(0);
        buffer.CellAt(0, 2).Character.Should().Be('A');
        buffer.CellAt(0, 3).Character.Should().Be('B');
        buffer.CellAt(0, 4).Character.Should().Be('\u4E2D');
        buffer.CellAt(0, 5).Width.Should().Be(0);
        buffer.CursorRow.Should().Be(0);
        buffer.CursorCol.Should().Be(6);
    }

    private static string GetScreenLine(TerminalBuffer buffer, int row)
    {
        var sb = new StringBuilder();
        for (int col = 0; col < buffer.Cols; col++)
        {
            var cell = buffer.CellAt(row, col);
            if (cell.Width == 0)
                continue;
            sb.Append(cell.Character == '\0' ? ' ' : cell.Character);
        }

        return sb.ToString().TrimEnd();
    }
}

public class TerminalWidthTests
{
    [Fact]
    public void GetWidth_ClassifiesAsciiWideAndCombining()
    {
        TerminalWidth.GetWidth('A').Should().Be(1);
        TerminalWidth.GetWidth('\u4E2D').Should().Be(2);
        TerminalWidth.GetWidth('\u0301').Should().Be(0);
    }
}

public class OscHandlerTests
{
    [Fact]
    public void Handle_Osc0_ChangesTitleEvent()
    {
        var handler = new OscHandler();
        string? title = null;
        handler.TitleChanged += t => title = t;

        handler.Handle("0;My Terminal Title");

        title.Should().Be("My Terminal Title");
    }

    [Fact]
    public void Handle_Osc7_ChangesWorkingDirectory()
    {
        var handler = new OscHandler();
        string? dir = null;
        handler.WorkingDirectoryChanged += d => dir = d;

        handler.Handle("7;file://localhost/C:/Users/test/project");

        dir.Should().NotBeNull();
    }

    [Fact]
    public void Handle_Osc9_FiresNotification()
    {
        var handler = new OscHandler();
        string? body = null;
        handler.NotificationReceived += (t, s, b) => body = b;

        handler.Handle("9;Agent is waiting for your input");

        body.Should().Be("Agent is waiting for your input");
    }

    [Fact]
    public void Handle_Osc99_KeyValue_ParsesCorrectly()
    {
        var handler = new OscHandler();
        string? title = null, body = null;
        handler.NotificationReceived += (t, s, b) => { title = t; body = b; };

        handler.Handle("99;t=Claude Code;b=Waiting for input");

        title.Should().Be("Claude Code");
        body.Should().Be("Waiting for input");
    }

    [Fact]
    public void Handle_Osc777_Notify_ParsesCorrectly()
    {
        var handler = new OscHandler();
        string? title = null, body = null;
        handler.NotificationReceived += (t, s, b) => { title = t; body = b; };

        handler.Handle("777;notify;Claude;Task completed");

        title.Should().Be("Claude");
        body.Should().Be("Task completed");
    }

    [Fact]
    public void Handle_Osc133_FiresPromptMarker()
    {
        var handler = new OscHandler();
        char? marker = null;
        handler.ShellPromptMarker += (m, payload) => marker = m;

        handler.Handle("133;A");

        marker.Should().Be('A');
    }
}

public class SplitNodeTests
{
    [Fact]
    public void CreateLeaf_IsLeaf()
    {
        var node = Cmux.Core.Models.SplitNode.CreateLeaf("pane-1");
        node.IsLeaf.Should().BeTrue();
        node.PaneId.Should().Be("pane-1");
    }

    [Fact]
    public void Split_TurnsLeafIntoContainer()
    {
        var node = Cmux.Core.Models.SplitNode.CreateLeaf("pane-1");

        var newChild = node.Split(Cmux.Core.Models.SplitDirection.Vertical);

        node.IsLeaf.Should().BeFalse();
        node.First.Should().NotBeNull();
        node.Second.Should().NotBeNull();
        node.First!.PaneId.Should().Be("pane-1");
        newChild.PaneId.Should().NotBeNull();
    }

    [Fact]
    public void Split_NonLeaf_ThrowsInvalidOperation()
    {
        var node = Cmux.Core.Models.SplitNode.CreateLeaf("pane-1");
        node.Split(Cmux.Core.Models.SplitDirection.Vertical);

        var act = () => node.Split(Cmux.Core.Models.SplitDirection.Horizontal);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void FindNode_FindsLeaf()
    {
        var node = Cmux.Core.Models.SplitNode.CreateLeaf("pane-1");
        node.Split(Cmux.Core.Models.SplitDirection.Vertical);

        var found = node.FindNode("pane-1");

        found.Should().NotBeNull();
        found!.PaneId.Should().Be("pane-1");
    }

    [Fact]
    public void GetLeaves_ReturnsAllLeaves()
    {
        var node = Cmux.Core.Models.SplitNode.CreateLeaf("pane-1");
        node.Split(Cmux.Core.Models.SplitDirection.Vertical);

        var leaves = node.GetLeaves().ToList();

        leaves.Should().HaveCount(2);
        leaves[0].PaneId.Should().Be("pane-1");
    }

    [Fact]
    public void Remove_CollapsesParent()
    {
        var node = Cmux.Core.Models.SplitNode.CreateLeaf("pane-1");
        var newChild = node.Split(Cmux.Core.Models.SplitDirection.Vertical);
        var newPaneId = newChild.PaneId!;

        bool removed = node.Remove(newPaneId);

        removed.Should().BeTrue();
        node.IsLeaf.Should().BeTrue();
        node.PaneId.Should().Be("pane-1");
    }

    [Fact]
    public void GetNextLeaf_CyclesCorrectly()
    {
        var node = Cmux.Core.Models.SplitNode.CreateLeaf("pane-1");
        var child2 = node.Split(Cmux.Core.Models.SplitDirection.Vertical);

        var next = node.GetNextLeaf("pane-1");
        next.Should().NotBeNull();
        next!.PaneId.Should().Be(child2.PaneId);

        // Wraps around
        var wrap = node.GetNextLeaf(child2.PaneId!);
        wrap.Should().NotBeNull();
        wrap!.PaneId.Should().Be("pane-1");
    }
}

public class TerminalColorTests
{
    [Fact]
    public void FromIndex_BasicColors_ReturnsExpected()
    {
        var black = TerminalColor.FromIndex(0);
        black.R.Should().Be(0);
        black.G.Should().Be(0);
        black.B.Should().Be(0);

        var white = TerminalColor.FromIndex(15);
        white.R.Should().Be(0xFF);
        white.G.Should().Be(0xFF);
        white.B.Should().Be(0xFF);
    }

    [Fact]
    public void FromIndex_256Colors_DoesNotThrow()
    {
        for (int i = 0; i < 256; i++)
        {
            var act = () => TerminalColor.FromIndex(i);
            act.Should().NotThrow();
        }
    }

    [Fact]
    public void FromRgb_StoresCorrectValues()
    {
        var color = TerminalColor.FromRgb(0x12, 0x34, 0x56);
        color.R.Should().Be(0x12);
        color.G.Should().Be(0x34);
        color.B.Should().Be(0x56);
        color.IsDefault.Should().BeFalse();
    }

    [Fact]
    public void Default_IsMarkedAsDefault()
    {
        var def = TerminalColor.Default;
        def.IsDefault.Should().BeTrue();
    }
}

public class TerminalSelectionTests
{
    [Fact]
    public void StartAndExtend_CreatesSelection()
    {
        var selection = new TerminalSelection();
        selection.StartSelection(0, 5);
        selection.ExtendSelection(0, 10);

        selection.HasSelection.Should().BeTrue();
        selection.IsSelected(0, 7).Should().BeTrue();
        selection.IsSelected(0, 12).Should().BeFalse();
    }

    [Fact]
    public void Clear_RemovesSelection()
    {
        var selection = new TerminalSelection();
        selection.StartSelection(0, 0);
        selection.ExtendSelection(0, 10);

        selection.ClearSelection();

        selection.HasSelection.Should().BeFalse();
    }

    [Fact]
    public void GetSelectedText_ExtractsCorrectly()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.WriteString("Hello World");

        var selection = new TerminalSelection();
        selection.StartSelection(0, 0);
        selection.ExtendSelection(0, 4);

        var text = selection.GetSelectedText(buffer);
        text.Should().Be("Hello");
    }

    [Fact]
    public void GetSelectedText_WideCharacter_DoesNotCopyContinuationCell()
    {
        var buffer = new TerminalBuffer(10, 1);
        buffer.WriteString("\u4E2DA");

        var selection = new TerminalSelection();
        selection.StartSelection(0, 0);
        selection.ExtendSelection(0, 2);

        var text = selection.GetSelectedText(buffer);
        text.Should().Be("\u4E2DA");
    }

    [Fact]
    public void RestoreAfterResize_ReflowsSelectionAcrossSoftWrappedLine()
    {
        var buffer = new TerminalBuffer(5, 3);
        buffer.WriteString("abcdefghij");

        var selection = new TerminalSelection();
        selection.StartSelection(0, 1);
        selection.ExtendSelection(1, 2);
        var snapshot = selection.CaptureForResize(buffer);

        buffer.Resize(10, 3);

        selection.RestoreAfterResize(buffer, snapshot!.Value).Should().BeTrue();
        selection.Start.Should().Be(new SelectionPoint(0, 1));
        selection.End.Should().Be(new SelectionPoint(0, 7));
        selection.GetSelectedText(buffer).Should().Be("bcdefgh");
    }

    [Fact]
    public void RestoreAfterResize_PreservesAnchorAtSoftWrapBoundary()
    {
        var buffer = new TerminalBuffer(5, 3);
        buffer.WriteString("abcdefghij");

        var selection = new TerminalSelection();
        selection.StartSelection(1, 0);
        selection.ExtendSelection(1, 2);
        var snapshot = selection.CaptureForResize(buffer);

        buffer.Resize(5, 3);

        selection.RestoreAfterResize(buffer, snapshot!.Value).Should().BeTrue();
        selection.Start.Should().Be(new SelectionPoint(1, 0));
        selection.End.Should().Be(new SelectionPoint(1, 2));
        selection.GetSelectedText(buffer).Should().Be("fgh");
    }

    [Fact]
    public void RestoreAfterResize_MapsSelectionCapturedFromScrollbackViewport()
    {
        var buffer = new TerminalBuffer(5, 2);
        buffer.WriteString("abcdefghijklmno");

        var selection = new TerminalSelection();
        selection.StartSelection(0, 2);
        selection.ExtendSelection(1, 1);
        var snapshot = selection.CaptureForResize(buffer, scrollOffset: -1);

        buffer.Resize(10, 2);

        selection.RestoreAfterResize(buffer, snapshot!.Value, scrollOffset: 0).Should().BeTrue();
        selection.Start.Should().Be(new SelectionPoint(0, 2));
        selection.End.Should().Be(new SelectionPoint(0, 6));
        selection.GetSelectedText(buffer).Should().Be("cdefg");
    }

    [Fact]
    public void RestoreAfterResize_PreservesSelectionAcrossHardLineBreaks()
    {
        var buffer = new TerminalBuffer(5, 3);
        buffer.WriteString("abc");
        buffer.NewLine();
        buffer.WriteString("def");

        var selection = new TerminalSelection();
        selection.StartSelection(0, 1);
        selection.ExtendSelection(1, 2);
        var snapshot = selection.CaptureForResize(buffer);

        buffer.Resize(10, 3);

        selection.RestoreAfterResize(buffer, snapshot!.Value).Should().BeTrue();
        selection.Start.Should().Be(new SelectionPoint(0, 1));
        selection.End.Should().Be(new SelectionPoint(1, 2));
        selection.GetSelectedText(buffer).Should().Be($"bc{Environment.NewLine}def");
    }

    [Fact]
    public void IsSelected_MultiLine_Works()
    {
        var selection = new TerminalSelection();
        selection.StartSelection(0, 5);
        selection.ExtendSelection(2, 10);

        selection.IsSelected(0, 6).Should().BeTrue();
        selection.IsSelected(1, 0).Should().BeTrue(); // Middle line, full
        selection.IsSelected(2, 5).Should().BeTrue();
        selection.IsSelected(2, 11).Should().BeFalse();
    }
}


public class AlternateScreenBufferTests
{
    [Fact]
    public void SwitchToAlternateScreen_ClearsAndSavesMainBuffer()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.WriteChar('X');
        buffer.CursorCol.Should().Be(1);

        buffer.SwitchToAlternateScreen();

        buffer.IsAlternateScreen.Should().BeTrue();
        buffer.CursorRow.Should().Be(0);
        buffer.CursorCol.Should().Be(0);
        buffer.CellAt(0, 0).Character.Should().Be(' ');
    }

    [Fact]
    public void SwitchToMainScreen_RestoresPreviousState()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.WriteChar('A');
        buffer.WriteChar('B');
        int savedCol = buffer.CursorCol;

        buffer.SwitchToAlternateScreen();
        buffer.WriteChar('Z');

        buffer.SwitchToMainScreen();

        buffer.IsAlternateScreen.Should().BeFalse();
        buffer.CursorCol.Should().Be(savedCol);
        buffer.CellAt(0, 0).Character.Should().Be('A');
        buffer.CellAt(0, 1).Character.Should().Be('B');
    }

    [Fact]
    public void SwitchToAlternateScreen_DoubleSwitchIsNoop()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.WriteChar('X');

        buffer.SwitchToAlternateScreen();
        buffer.WriteChar('Y');

        buffer.SwitchToAlternateScreen();

        buffer.CellAt(0, 0).Character.Should().Be('Y');
    }

    [Fact]
    public void SwitchToMainScreen_WhenNotAlternate_IsNoop()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.WriteChar('X');

        buffer.SwitchToMainScreen();

        buffer.IsAlternateScreen.Should().BeFalse();
        buffer.CellAt(0, 0).Character.Should().Be('X');
    }
}

public class TerminalModeTests
{
    [Fact]
    public void ApplicationCursorKeys_DefaultsToFalse()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.ApplicationCursorKeys.Should().BeFalse();
    }

    [Fact]
    public void BracketedPasteMode_DefaultsToFalse()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.BracketedPasteMode.Should().BeFalse();
    }

    [Fact]
    public void ApplicationCursorKeys_CanBeSet()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.ApplicationCursorKeys = true;
        buffer.ApplicationCursorKeys.Should().BeTrue();
    }

    [Fact]
    public void BracketedPasteMode_CanBeSet()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.BracketedPasteMode = true;
        buffer.BracketedPasteMode.Should().BeTrue();
    }

    [Fact]
    public void KeyboardModeFlags_DefaultToExpectedValues()
    {
        var buffer = new TerminalBuffer(80, 24);

        buffer.ApplicationKeypad.Should().BeFalse();
        buffer.AutoNewlineMode.Should().BeFalse();
        buffer.FocusEventMode.Should().BeFalse();
        buffer.AltSendsEscape.Should().BeTrue();
        buffer.MetaSendsEscape.Should().BeTrue();
        buffer.MouseAlternateScroll.Should().BeFalse();
    }
}

public class TerminalSessionModeTests
{
    [Fact]
    public void Feed_EscDeckpamAndDeckpnm_TogglesApplicationKeypad()
    {
        using var session = new TerminalSession("test");

        session.FeedOutput(Encoding.UTF8.GetBytes("\x1b="));
        session.Buffer.ApplicationKeypad.Should().BeTrue();

        session.FeedOutput(Encoding.UTF8.GetBytes("\x1b>"));
        session.Buffer.ApplicationKeypad.Should().BeFalse();
    }

    [Fact]
    public void Feed_PrivateKeyboardModes_TogglesBufferFlags()
    {
        using var session = new TerminalSession("test");

        session.FeedOutput(Encoding.UTF8.GetBytes("\x1b[?66;1004;1007;1036;1039h"));

        session.Buffer.ApplicationKeypad.Should().BeTrue();
        session.Buffer.FocusEventMode.Should().BeTrue();
        session.Buffer.MouseAlternateScroll.Should().BeTrue();
        session.Buffer.MetaSendsEscape.Should().BeTrue();
        session.Buffer.AltSendsEscape.Should().BeTrue();

        session.FeedOutput(Encoding.UTF8.GetBytes("\x1b[?66;1004;1007;1036;1039l"));

        session.Buffer.ApplicationKeypad.Should().BeFalse();
        session.Buffer.FocusEventMode.Should().BeFalse();
        session.Buffer.MouseAlternateScroll.Should().BeFalse();
        session.Buffer.MetaSendsEscape.Should().BeFalse();
        session.Buffer.AltSendsEscape.Should().BeFalse();
    }

    [Fact]
    public void Feed_LineFeedNewLineMode_TogglesAutoNewlineMode()
    {
        using var session = new TerminalSession("test");

        session.FeedOutput(Encoding.UTF8.GetBytes("\x1b[20h"));
        session.Buffer.AutoNewlineMode.Should().BeTrue();

        session.FeedOutput(Encoding.UTF8.GetBytes("\x1b[20l"));
        session.Buffer.AutoNewlineMode.Should().BeFalse();
    }

    [Fact]
    public void Feed_Private1048_RestoresSavedCursor()
    {
        using var session = new TerminalSession("test");
        session.Buffer.MoveCursorTo(2, 3);

        session.FeedOutput(Encoding.UTF8.GetBytes("\x1b[?1048h"));
        session.Buffer.MoveCursorTo(0, 0);
        session.FeedOutput(Encoding.UTF8.GetBytes("\x1b[?1048l"));

        session.Buffer.CursorRow.Should().Be(2);
        session.Buffer.CursorCol.Should().Be(3);
    }
}

public class TerminalKeyEncoderTests
{
    [Fact]
    public void Encode_ArrowKey_UsesAnsiCursorByDefault()
    {
        var sequence = TerminalKeyEncoder.Encode(TerminalKey.Left);

        sequence.Should().Be("\x1b[D");
    }

    [Fact]
    public void Encode_ArrowKey_UsesApplicationCursorWhenEnabled()
    {
        var sequence = TerminalKeyEncoder.Encode(
            TerminalKey.Left,
            options: new TerminalKeyEncodingOptions(ApplicationCursorKeys: true));

        sequence.Should().Be("\x1bOD");
    }

    [Theory]
    [InlineData(TerminalKey.Left, TerminalKeyModifiers.Control, "\x1b[1;5D")]
    [InlineData(TerminalKey.Right, TerminalKeyModifiers.Alt, "\x1b[1;3C")]
    [InlineData(TerminalKey.Up, TerminalKeyModifiers.Shift, "\x1b[1;2A")]
    [InlineData(TerminalKey.Down, TerminalKeyModifiers.Control | TerminalKeyModifiers.Alt, "\x1b[1;7B")]
    public void Encode_CursorKeyWithModifiers_UsesXtermModifierEncoding(
        TerminalKey key,
        TerminalKeyModifiers modifiers,
        string expected)
    {
        var sequence = TerminalKeyEncoder.Encode(key, modifiers);

        sequence.Should().Be(expected);
    }

    [Fact]
    public void Encode_ApplicationCursorWithModifier_ConvertsSs3ToCsi()
    {
        var sequence = TerminalKeyEncoder.Encode(
            TerminalKey.Left,
            TerminalKeyModifiers.Control,
            new TerminalKeyEncodingOptions(ApplicationCursorKeys: true));

        sequence.Should().Be("\x1b[1;5D");
    }

    [Theory]
    [InlineData(TerminalKey.F1, TerminalKeyModifiers.Shift, "\x1b[1;2P")]
    [InlineData(TerminalKey.F5, TerminalKeyModifiers.Control, "\x1b[15;5~")]
    [InlineData(TerminalKey.Delete, TerminalKeyModifiers.Alt, "\x1b[3;3~")]
    public void Encode_FunctionKeyWithModifiers_UsesXtermModifierEncoding(
        TerminalKey key,
        TerminalKeyModifiers modifiers,
        string expected)
    {
        var sequence = TerminalKeyEncoder.Encode(key, modifiers);

        sequence.Should().Be(expected);
    }

    [Fact]
    public void Encode_ShiftTab_ReturnsReverseTab()
    {
        var sequence = TerminalKeyEncoder.Encode(TerminalKey.Tab, TerminalKeyModifiers.Shift);

        sequence.Should().Be("\x1b[Z");
    }

    [Fact]
    public void TryEncodeControlLetter_ReturnsControlByte()
    {
        var encoded = TerminalKeyEncoder.TryEncodeControlLetter('x', out var sequence);

        encoded.Should().BeTrue();
        sequence.Should().Be("\x18");
    }

    [Fact]
    public void Encode_Keypad_UsesNumericModeByDefault()
    {
        TerminalKeyEncoder.Encode(TerminalKey.Keypad1).Should().Be("1");
        TerminalKeyEncoder.Encode(TerminalKey.KeypadAdd).Should().Be("+");
        TerminalKeyEncoder.Encode(TerminalKey.KeypadEnter).Should().Be("\r");
    }

    [Fact]
    public void Encode_Keypad_UsesApplicationModeWhenEnabled()
    {
        var options = new TerminalKeyEncodingOptions(ApplicationKeypad: true);

        TerminalKeyEncoder.Encode(TerminalKey.Keypad1, options: options).Should().Be("\x1bOq");
        TerminalKeyEncoder.Encode(TerminalKey.KeypadAdd, options: options).Should().Be("\x1bOk");
        TerminalKeyEncoder.Encode(TerminalKey.KeypadEnter, options: options).Should().Be("\x1bOM");
    }

    [Fact]
    public void Encode_Enter_UsesAutoNewlineWhenEnabled()
    {
        var sequence = TerminalKeyEncoder.Encode(
            TerminalKey.Enter,
            options: new TerminalKeyEncodingOptions(AutoNewline: true));

        sequence.Should().Be("\r\n");
    }
}

public class UrlDetectorTests
{
    [Fact]
    public void FindUrls_DetectsHttps()
    {
        var urls = UrlDetector.FindUrls("Visit https://example.com/path for info");
        urls.Should().HaveCount(1);
        urls[0].url.Should().Be("https://example.com/path");
        urls[0].startCol.Should().Be(6);
    }

    [Fact]
    public void FindUrls_DetectsMultipleUrls()
    {
        var urls = UrlDetector.FindUrls("Go to http://a.com and https://b.io/x");
        urls.Should().HaveCount(2);
    }

    [Fact]
    public void FindUrls_NoUrlsReturnsEmpty()
    {
        var urls = UrlDetector.FindUrls("No urls here just text");
        urls.Should().BeEmpty();
    }

    [Fact]
    public void GetRowText_ExtractsBufferRow()
    {
        var buffer = new TerminalBuffer(10, 1);
        buffer.WriteChar('H');
        buffer.WriteChar('i');
        var text = UrlDetector.GetRowText(buffer, 0);
        text.Should().StartWith("Hi");
        text.Should().HaveLength(10);
    }
}

public class MouseModeTests
{
    [Fact]
    public void MouseTrackingModes_DefaultToFalse()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.MouseTrackingNormal.Should().BeFalse();
        buffer.MouseTrackingButton.Should().BeFalse();
        buffer.MouseTrackingAny.Should().BeFalse();
        buffer.MouseSgrExtended.Should().BeFalse();
        buffer.MouseEnabled.Should().BeFalse();
    }

    [Fact]
    public void MouseEnabled_TrueWhenAnyTrackingSet()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.MouseTrackingNormal = true;
        buffer.MouseEnabled.Should().BeTrue();
    }

    [Fact]
    public void MouseEnabled_TrueWhenButtonTrackingSet()
    {
        var buffer = new TerminalBuffer(80, 24);
        buffer.MouseTrackingButton = true;
        buffer.MouseEnabled.Should().BeTrue();
    }
}

public class AgentConversationStoreMessageParsingTests
{
    private static readonly MethodInfo ReadMessagesMethod = typeof(AgentConversationStoreService)
        .GetMethod("ReadMessagesFromFile", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly JsonSerializerOptions CamelCaseIndented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions CamelCaseCompact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    [Fact]
    public void ReadMessagesFromFile_ParsesMultilineObjects_WithUtf8Bom()
    {
        var message1 = new AgentConversationMessage
        {
            Id = "m1",
            ThreadId = "t1",
            Role = "user",
            Content = "hello",
            CreatedAtUtc = new DateTime(2026, 2, 27, 12, 0, 0, DateTimeKind.Utc),
        };
        var message2 = new AgentConversationMessage
        {
            Id = "m2",
            ThreadId = "t1",
            Role = "assistant",
            Content = "hi",
            CreatedAtUtc = new DateTime(2026, 2, 27, 12, 0, 5, DateTimeKind.Utc),
        };

        var json = string.Join(
            Environment.NewLine,
            JsonSerializer.Serialize(message1, CamelCaseIndented),
            JsonSerializer.Serialize(message2, CamelCaseIndented)) + Environment.NewLine;

        var path = Path.Combine(Path.GetTempPath(), $"cmux-agent-{Guid.NewGuid():N}.jsonl");
        try
        {
            var payload = Encoding.UTF8.GetBytes(json);
            var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(payload).ToArray();
            File.WriteAllBytes(path, bytes);

            var output = new List<AgentConversationMessage>();
            ReadMessagesMethod.Invoke(null, [path, output]);

            output.Should().HaveCount(2);
            output[0].Id.Should().Be("m1");
            output[1].Id.Should().Be("m2");
            output[0].Content.Should().Be("hello");
            output[1].Content.Should().Be("hi");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void ReadMessagesFromFile_FallbackLineParser_HandlesBomOnFirstLine()
    {
        var message1 = new AgentConversationMessage
        {
            Id = "line1",
            ThreadId = "t2",
            Role = "user",
            Content = "first",
            CreatedAtUtc = new DateTime(2026, 2, 27, 12, 1, 0, DateTimeKind.Utc),
        };
        var message2 = new AgentConversationMessage
        {
            Id = "line2",
            ThreadId = "t2",
            Role = "assistant",
            Content = "second",
            CreatedAtUtc = new DateTime(2026, 2, 27, 12, 1, 5, DateTimeKind.Utc),
        };

        var line1 = JsonSerializer.Serialize(message1, CamelCaseCompact);
        var line2 = JsonSerializer.Serialize(message2, CamelCaseCompact);
        var malformed = "{\"broken\": }";
        var content = string.Join(Environment.NewLine, line1, malformed, line2) + Environment.NewLine;

        var path = Path.Combine(Path.GetTempPath(), $"cmux-agent-{Guid.NewGuid():N}.jsonl");
        try
        {
            var payload = Encoding.UTF8.GetBytes(content);
            var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(payload).ToArray();
            File.WriteAllBytes(path, bytes);

            var output = new List<AgentConversationMessage>();
            ReadMessagesMethod.Invoke(null, [path, output]);

            output.Should().HaveCount(2);
            output.Select(m => m.Id).Should().ContainInOrder("line1", "line2");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
