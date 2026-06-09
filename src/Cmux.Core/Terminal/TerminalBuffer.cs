namespace Cmux.Core.Terminal;

/// <summary>
/// Manages the terminal cell grid, cursor state, scrollback buffer,
/// and scroll regions. This is the core data structure that the VT parser
/// operates on and the renderer reads from.
/// </summary>
public class TerminalBuffer
{
    private TerminalCell[,] _cells;
    private readonly ScrollbackBuffer<TerminalCell[]> _scrollback;
    private readonly ScrollbackBuffer<bool> _scrollbackWrapped;
    private readonly int _maxScrollback;
    private bool[] _rowWrapped;

    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int CursorRow { get; set; }
    public int CursorCol { get; set; }
    public bool CursorVisible { get; set; } = true;

    // Scroll region (inclusive, 0-based)
    public int ScrollTop { get; private set; }
    public int ScrollBottom { get; private set; }

    // Saved cursor state
    private int _savedCursorRow;
    private int _savedCursorCol;
    private TerminalAttribute _savedAttribute;

    // Current writing attribute
    public TerminalAttribute CurrentAttribute { get; set; } = TerminalAttribute.Default;

    // Mode flags
    public bool OriginMode { get; set; }
    public bool AutoWrapMode { get; set; } = true;
    public bool InsertMode { get; set; }
    public bool ApplicationCursorKeys { get; set; }
    public bool ApplicationKeypad { get; set; }
    public bool AutoNewlineMode { get; set; }
    public bool BracketedPasteMode { get; set; }
    public bool FocusEventMode { get; set; }
    public bool AltSendsEscape { get; set; } = true;
    public bool MetaSendsEscape { get; set; } = true;
    public bool IsAlternateScreen { get; private set; }

    // Mouse tracking modes
    public bool MouseTrackingNormal { get; set; }    // Mode 1000: button events
    public bool MouseTrackingButton { get; set; }    // Mode 1002: button + motion while pressed
    public bool MouseTrackingAny { get; set; }       // Mode 1003: all motion
    public bool MouseSgrExtended { get; set; }       // Mode 1006: SGR extended coordinates
    public bool MouseAlternateScroll { get; set; }   // Mode 1007: wheel sends cursor keys in alternate screen
    public bool MouseEnabled => MouseTrackingNormal || MouseTrackingButton || MouseTrackingAny;

    private bool _wrapPending;

    // Alternate screen buffer state
    private TerminalCell[,]? _savedMainCells;
    private List<TerminalCell[]>? _savedMainScrollbackList;
    private List<bool>? _savedMainScrollbackWrappedList;
    private bool[]? _savedMainRowWrapped;
    private int _savedMainCursorRow;
    private int _savedMainCursorCol;
    private TerminalAttribute _savedMainAttribute;

    public int ScrollbackCount => _scrollback.Count;
    public int TotalLines => Rows + _scrollback.Count;

    public event Action? ContentChanged;

    public TerminalBuffer(int cols, int rows, int maxScrollback = 10_000)
    {
        Cols = Math.Max(1, cols);
        Rows = Math.Max(1, rows);
        _maxScrollback = maxScrollback;
        _scrollback = new ScrollbackBuffer<TerminalCell[]>(maxScrollback);
        _scrollbackWrapped = new ScrollbackBuffer<bool>(maxScrollback);
        ScrollTop = 0;
        ScrollBottom = Rows - 1;
        _cells = new TerminalCell[Rows, Cols];
        _rowWrapped = new bool[Rows];
        Clear();
    }

    public void Clear()
    {
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                _cells[r, c] = TerminalCell.Empty;

        Array.Clear(_rowWrapped, 0, _rowWrapped.Length);
    }

    public void ResetModes()
    {
        OriginMode = false;
        AutoWrapMode = true;
        InsertMode = false;
        ApplicationCursorKeys = false;
        ApplicationKeypad = false;
        AutoNewlineMode = false;
        BracketedPasteMode = false;
        FocusEventMode = false;
        AltSendsEscape = true;
        MetaSendsEscape = true;
        CursorVisible = true;
        MouseTrackingNormal = false;
        MouseTrackingButton = false;
        MouseTrackingAny = false;
        MouseSgrExtended = false;
        MouseAlternateScroll = false;
    }

    public ref TerminalCell CellAt(int row, int col)
    {
        int maxRow = _cells.GetLength(0) - 1;
        int maxCol = _cells.GetLength(1) - 1;
        row = Math.Clamp(row, 0, maxRow);
        col = Math.Clamp(col, 0, maxCol);
        return ref _cells[row, col];
    }

    public TerminalCell[] GetLine(int row)
    {
        int cols = _cells.GetLength(1);
        int safeRow = Math.Clamp(row, 0, _cells.GetLength(0) - 1);
        var line = new TerminalCell[cols];
        for (int c = 0; c < cols; c++)
            line[c] = _cells[safeRow, c];
        return line;
    }

    public TerminalCell[]? GetScrollbackLine(int index)
    {
        if (index < 0 || index >= _scrollback.Count) return null;
        return _scrollback[index];
    }

    public void SetChar(int row, int col, char ch, TerminalAttribute attr)
    {
        if (row < 0 || row >= Rows || col < 0 || col >= Cols) return;
        var displayWidth = TerminalWidth.GetWidth(ch);
        if (displayWidth <= 0) return;
        var width = Math.Min(displayWidth, Cols - col);
        if (width <= 0) return;

        ClearCellForWrite(row, col);
        if (width == 2)
            ClearCellForWrite(row, col + 1);

        _cells[row, col] = new TerminalCell
        {
            Character = ch,
            Attribute = attr,
            IsDirty = true,
            Width = width,
        };

        if (width == 2)
            _cells[row, col + 1] = CreateContinuationCell(attr);
    }

    /// <summary>
    /// Writes a character at the current cursor position and advances the cursor.
    /// Handles auto-wrap and insert mode.
    /// </summary>
    public void WriteChar(char c)
    {
        if (!ClampCursorToBounds())
            return;

        var displayWidth = TerminalWidth.GetWidth(c);
        if (displayWidth <= 0)
            return;
        var width = Math.Min(displayWidth, Cols);

        if (_wrapPending && AutoWrapMode)
        {
            _rowWrapped[CursorRow] = true;
            CarriageReturn();
            LineFeed();
            _wrapPending = false;
        }

        if (width == 2 && CursorCol == Cols - 1)
        {
            if (!AutoWrapMode)
                return;

            CarriageReturn();
            LineFeed();
            if (!ClampCursorToBounds())
                return;
        }

        if (InsertMode)
            ShiftCellsRight(CursorRow, CursorCol, width);

        if (CursorRow >= 0 && CursorRow < Rows && CursorCol >= 0 && CursorCol < Cols)
        {
            ClearCellForWrite(CursorRow, CursorCol);
            if (width == 2)
                ClearCellForWrite(CursorRow, CursorCol + 1);

            _cells[CursorRow, CursorCol] = new TerminalCell
            {
                Character = c,
                Attribute = CurrentAttribute,
                IsDirty = true,
                Width = width,
            };

            if (width == 2)
                _cells[CursorRow, CursorCol + 1] = CreateContinuationCell(CurrentAttribute);
        }

        if (CursorCol + width >= Cols)
        {
            CursorCol = Cols - 1;
            _wrapPending = true;
        }
        else
        {
            CursorCol += width;
        }
    }

    public void WriteString(string text)
    {
        foreach (var c in text)
            WriteChar(c);
    }

    public void CarriageReturn()
    {
        CursorCol = 0;
        _wrapPending = false;
    }

    public void LineFeed()
    {
        _wrapPending = false;
        if (CursorRow == ScrollBottom)
        {
            ScrollUp(1);
        }
        else if (CursorRow < Rows - 1)
        {
            CursorRow++;
        }
    }

    public void ReverseLineFeed()
    {
        if (CursorRow == ScrollTop)
        {
            ScrollDown(1);
        }
        else if (CursorRow > 0)
        {
            CursorRow--;
        }
    }

    public void NewLine()
    {
        CarriageReturn();
        LineFeed();
    }

    /// <summary>
    /// Scrolls the scroll region up by the given number of lines.
    /// Lines scrolled out of the top go to scrollback if the scroll region is the full screen.
    /// </summary>
    public void ScrollUp(int lines = 1)
    {
        for (int n = 0; n < lines; n++)
        {
            // If the scroll region starts at line 0, push to scrollback
            if (ScrollTop == 0)
            {
                var scrolledLine = new TerminalCell[Cols];
                for (int c = 0; c < Cols; c++)
                    scrolledLine[c] = _cells[0, c];

                _scrollback.Add(scrolledLine);
                _scrollbackWrapped.Add(_rowWrapped[0]);
            }

            // Shift lines up within the scroll region
            for (int r = ScrollTop; r < ScrollBottom; r++)
                _rowWrapped[r] = _rowWrapped[r + 1];
            for (int r = ScrollTop; r < ScrollBottom; r++)
                for (int c = 0; c < Cols; c++)
                    _cells[r, c] = _cells[r + 1, c];

            // Clear the bottom line
            _rowWrapped[ScrollBottom] = false;
            for (int c = 0; c < Cols; c++)
                _cells[ScrollBottom, c] = TerminalCell.Empty;
        }

        RaiseContentChanged();
    }

    /// <summary>
    /// Scrolls the scroll region down by the given number of lines.
    /// </summary>
    public void ScrollDown(int lines = 1)
    {
        for (int n = 0; n < lines; n++)
        {
            for (int r = ScrollBottom; r > ScrollTop; r--)
                _rowWrapped[r] = _rowWrapped[r - 1];
            for (int r = ScrollBottom; r > ScrollTop; r--)
                for (int c = 0; c < Cols; c++)
                    _cells[r, c] = _cells[r - 1, c];

            _rowWrapped[ScrollTop] = false;
            for (int c = 0; c < Cols; c++)
                _cells[ScrollTop, c] = TerminalCell.Empty;
        }

        RaiseContentChanged();
    }

    /// <summary>
    /// Erases parts of the display.
    /// 0 = cursor to end, 1 = start to cursor, 2 = all, 3 = all + scrollback
    /// </summary>
    public void EraseInDisplay(int mode)
    {
        if (!ClampCursorToBounds())
            return;

        switch (mode)
        {
            case 0: // Cursor to end
                ClearRange(CursorRow, CursorCol, Cols - 1);
                _rowWrapped[CursorRow] = false;
                for (int r = CursorRow + 1; r < Rows; r++)
                {
                    _rowWrapped[r] = false;
                    ClearRange(r, 0, Cols - 1);
                }
                break;
            case 1: // Start to cursor
                for (int r = 0; r < CursorRow; r++)
                {
                    _rowWrapped[r] = false;
                    ClearRange(r, 0, Cols - 1);
                }
                ClearRange(CursorRow, 0, CursorCol);
                break;
            case 2: // All
                Clear();
                break;
            case 3: // All + scrollback
                Clear();
                _scrollback.Clear();
                _scrollbackWrapped.Clear();
                break;
        }

        RaiseContentChanged();
    }

    /// <summary>
    /// Erases parts of the current line.
    /// 0 = cursor to end, 1 = start to cursor, 2 = entire line
    /// </summary>
    public void EraseInLine(int mode)
    {
        if (!ClampCursorToBounds())
            return;

        switch (mode)
        {
            case 0:
                ClearRange(CursorRow, CursorCol, Cols - 1);
                _rowWrapped[CursorRow] = false;
                break;
            case 1:
                ClearRange(CursorRow, 0, CursorCol);
                break;
            case 2:
                ClearRange(CursorRow, 0, Cols - 1);
                _rowWrapped[CursorRow] = false;
                break;
        }

        RaiseContentChanged();
    }

    public void EraseChars(int count)
    {
        if (!ClampCursorToBounds())
            return;

        count = Math.Max(0, count);
        if (count == 0)
            return;

        ClearRange(CursorRow, CursorCol, Math.Min(Cols - 1, CursorCol + count - 1));
        if (CursorCol + count >= Cols)
            _rowWrapped[CursorRow] = false;
        RaiseContentChanged();
    }

    public void InsertLines(int count)
    {
        if (!ClampCursorToBounds())
            return;

        count = Math.Max(0, count);
        int savedBottom = ScrollBottom;
        ScrollBottom = Rows - 1;
        for (int n = 0; n < count; n++)
        {
            for (int r = ScrollBottom; r > CursorRow; r--)
                _rowWrapped[r] = _rowWrapped[r - 1];
            for (int r = ScrollBottom; r > CursorRow; r--)
                for (int c = 0; c < Cols; c++)
                    _cells[r, c] = _cells[r - 1, c];
            _rowWrapped[CursorRow] = false;
            for (int c = 0; c < Cols; c++)
                _cells[CursorRow, c] = TerminalCell.Empty;
        }
        ScrollBottom = savedBottom;
        RaiseContentChanged();
    }

    public void DeleteLines(int count)
    {
        if (!ClampCursorToBounds())
            return;

        count = Math.Max(0, count);
        for (int n = 0; n < count; n++)
        {
            for (int r = CursorRow; r < ScrollBottom; r++)
                _rowWrapped[r] = _rowWrapped[r + 1];
            for (int r = CursorRow; r < ScrollBottom; r++)
                for (int c = 0; c < Cols; c++)
                    _cells[r, c] = _cells[r + 1, c];
            _rowWrapped[ScrollBottom] = false;
            for (int c = 0; c < Cols; c++)
                _cells[ScrollBottom, c] = TerminalCell.Empty;
        }
        RaiseContentChanged();
    }

    public void InsertChars(int count)
    {
        if (!ClampCursorToBounds())
            return;

        count = Math.Max(0, count);
        for (int n = 0; n < count; n++)
        {
            for (int c = Cols - 1; c > CursorCol; c--)
                _cells[CursorRow, c] = _cells[CursorRow, c - 1];
            _cells[CursorRow, CursorCol] = TerminalCell.Empty;
        }
        NormalizeWideCells(CursorRow);
        _rowWrapped[CursorRow] = false;
        RaiseContentChanged();
    }

    public void DeleteChars(int count)
    {
        if (!ClampCursorToBounds())
            return;

        count = Math.Max(0, count);
        for (int n = 0; n < count; n++)
        {
            for (int c = CursorCol; c < Cols - 1; c++)
                _cells[CursorRow, c] = _cells[CursorRow, c + 1];
            _cells[CursorRow, Cols - 1] = TerminalCell.Empty;
        }
        NormalizeWideCells(CursorRow);
        _rowWrapped[CursorRow] = false;
        RaiseContentChanged();
    }

    public void SetScrollRegion(int top, int bottom)
    {
        ScrollTop = Math.Max(0, Math.Min(top, Rows - 1));
        ScrollBottom = Math.Max(0, Math.Min(bottom, Rows - 1));
        if (ScrollTop > ScrollBottom)
            (ScrollTop, ScrollBottom) = (ScrollBottom, ScrollTop);
    }

    public void ResetScrollRegion()
    {
        ScrollTop = 0;
        ScrollBottom = Rows - 1;
    }

    public void SaveCursor()
    {
        _savedCursorRow = CursorRow;
        _savedCursorCol = CursorCol;
        _savedAttribute = CurrentAttribute;
    }

    public void RestoreCursor()
    {
        CursorRow = _savedCursorRow;
        CursorCol = _savedCursorCol;
        CurrentAttribute = _savedAttribute;
    }

    /// <summary>
    /// Switches to the alternate screen buffer (DECSET 1049).
    /// Saves main screen cells, scrollback, cursor, and attribute.
    /// </summary>
    public void SwitchToAlternateScreen()
    {
        if (IsAlternateScreen) return;

        // Save main screen state
        _savedMainCells = _cells;
        _savedMainScrollbackList = _scrollback.ToList();
        _savedMainScrollbackWrappedList = _scrollbackWrapped.ToList();
        _savedMainRowWrapped = _rowWrapped.ToArray();
        _savedMainCursorRow = CursorRow;
        _savedMainCursorCol = CursorCol;
        _savedMainAttribute = CurrentAttribute;

        // Create a fresh screen
        _cells = new TerminalCell[Rows, Cols];
        _rowWrapped = new bool[Rows];
        Clear();
        _scrollback.Clear();
        _scrollbackWrapped.Clear();

        CursorRow = 0;
        CursorCol = 0;
        CurrentAttribute = TerminalAttribute.Default;
        SetScrollRegion(0, Rows - 1);
        IsAlternateScreen = true;
    }

    /// <summary>
    /// Switches back to the main screen buffer (DECRST 1049).
    /// Restores saved main screen state.
    /// </summary>
    public void SwitchToMainScreen()
    {
        if (!IsAlternateScreen) return;

        // Restore main screen state
        if (_savedMainCells != null)
        {
            _cells = _savedMainCells;
            _savedMainCells = null;
        }

        if (_savedMainRowWrapped != null)
        {
            _rowWrapped = _savedMainRowWrapped;
            _savedMainRowWrapped = null;
        }

        _scrollback.Clear();
        _scrollbackWrapped.Clear();
        if (_savedMainScrollbackList != null)
        {
            _scrollback.AddRange(_savedMainScrollbackList);
            _savedMainScrollbackList = null;
        }
        if (_savedMainScrollbackWrappedList != null)
        {
            _scrollbackWrapped.AddRange(_savedMainScrollbackWrappedList);
            _savedMainScrollbackWrappedList = null;
        }

        CursorRow = _savedMainCursorRow;
        CursorCol = _savedMainCursorCol;
        CurrentAttribute = _savedMainAttribute;
        SetScrollRegion(0, Rows - 1);
        IsAlternateScreen = false;

        RaiseContentChanged();
    }

    public void MoveCursorTo(int row, int col)
    {
        CursorRow = Math.Clamp(row, 0, Rows - 1);
        CursorCol = Math.Clamp(col, 0, Cols - 1);
        _wrapPending = false;
    }

    public void MoveCursorUp(int count = 1)
    {
        CursorRow = Math.Max(ScrollTop, CursorRow - count);
        _wrapPending = false;
    }

    public void MoveCursorDown(int count = 1)
    {
        CursorRow = Math.Min(ScrollBottom, CursorRow + count);
        _wrapPending = false;
    }

    public void MoveCursorForward(int count = 1)
    {
        CursorCol = Math.Min(Cols - 1, CursorCol + count);
        _wrapPending = false;
    }

    public void MoveCursorBackward(int count = 1)
    {
        CursorCol = Math.Max(0, CursorCol - count);
        _wrapPending = false;
    }

    public void Tab()
    {
        int nextTab = ((CursorCol / 8) + 1) * 8;
        CursorCol = Math.Min(nextTab, Cols - 1);
    }

    public void Backspace()
    {
        if (CursorCol > 0)
            CursorCol--;
        _wrapPending = false;
    }

    /// <summary>
    /// Resizes the buffer and reflows soft-wrapped lines where wrap metadata is available.
    /// </summary>
    public void Resize(int newCols, int newRows)
    {
        newCols = Math.Max(1, newCols);
        newRows = Math.Max(1, newRows);

        var oldCursorRow = CursorRow;
        var resizeRows = CollectResizeRows();
        var logicalLines = BuildLogicalLinesForResize(
            resizeRows,
            _scrollback.Count + CursorRow,
            CursorCol,
            out var cursorLogicalLine,
            out var cursorLogicalOffset);
        var reflowedRows = ReflowLogicalLines(
            logicalLines,
            newCols,
            cursorLogicalLine,
            cursorLogicalOffset,
            out var reflowedCursorRow,
            out var reflowedCursorCol);

        var newCells = new TerminalCell[newRows, newCols];
        for (int r = 0; r < newRows; r++)
            for (int c = 0; c < newCols; c++)
                newCells[r, c] = TerminalCell.Empty;

        var newRowWrapped = new bool[newRows];
        var firstScreenRow = CalculateFirstScreenRow(reflowedRows.Count, newRows, reflowedCursorRow, oldCursorRow);

        _scrollback.Clear();
        _scrollbackWrapped.Clear();
        for (int i = 0; i < firstScreenRow; i++)
        {
            _scrollback.Add(reflowedRows[i].Cells);
            _scrollbackWrapped.Add(reflowedRows[i].Wrapped);
        }

        for (int sourceRow = firstScreenRow; sourceRow < reflowedRows.Count; sourceRow++)
        {
            var targetRow = sourceRow - firstScreenRow;
            if (targetRow >= newRows)
                break;

            for (int c = 0; c < newCols; c++)
                newCells[targetRow, c] = reflowedRows[sourceRow].Cells[c];
            newRowWrapped[targetRow] = reflowedRows[sourceRow].Wrapped;
        }

        _cells = newCells;
        _rowWrapped = newRowWrapped;
        Cols = newCols;
        Rows = newRows;
        for (int r = 0; r < Rows; r++)
            NormalizeWideCells(r);
        ScrollTop = 0;
        ScrollBottom = newRows - 1;
        CursorRow = Math.Clamp(reflowedCursorRow - firstScreenRow, 0, newRows - 1);
        CursorCol = Math.Clamp(reflowedCursorCol, 0, newCols - 1);
        _wrapPending = false;

        RaiseContentChanged();
    }

    public TerminalTextAnchor CreateTextAnchor(int visualRow, int col, int scrollOffset = 0)
    {
        var absoluteRow = _scrollback.Count + scrollOffset + visualRow;
        return CreateTextAnchorFromAbsolute(absoluteRow, col);
    }

    public bool TryResolveTextAnchor(
        TerminalTextAnchor anchor,
        int scrollOffset,
        out SelectionPoint point)
    {
        point = default;

        if (!TryResolveTextAnchorAbsolute(anchor, out var absoluteRow, out var col))
            return false;

        point = new SelectionPoint(absoluteRow - (_scrollback.Count + scrollOffset), col);
        return true;
    }

    public bool TryResolveTextAnchorAbsolute(
        TerminalTextAnchor anchor,
        out int absoluteRow,
        out int col)
    {
        absoluteRow = 0;
        col = 0;

        var rows = CollectResizeRows();
        var logicalLine = 0;
        var logicalOffset = 0;

        foreach (var row in rows)
        {
            var cells = ExtractLogicalCells(row.Cells, trimTrailingSpaces: !row.Wrapped);
            var rowWidth = GetDisplayWidth(cells);

            var rowEndOffset = logicalOffset + rowWidth;
            if (logicalLine == anchor.LogicalLine &&
                (anchor.DisplayOffset < rowEndOffset || !row.Wrapped))
            {
                absoluteRow = row.AbsoluteRow;
                col = GetColumnForDisplayOffset(row.Cells, anchor.DisplayOffset - logicalOffset);
                return true;
            }

            logicalOffset += rowWidth;
            if (!row.Wrapped)
            {
                logicalLine++;
                logicalOffset = 0;
            }
        }

        if (rows.Count == 0)
            return false;

        var last = rows[^1];
        absoluteRow = last.AbsoluteRow;
        col = GetLastSelectableColumn(last.Cells);
        return anchor.LogicalLine <= logicalLine;
    }

    private TerminalTextAnchor CreateTextAnchorFromAbsolute(int absoluteRow, int col)
    {
        var rows = CollectResizeRows();
        var logicalLine = 0;
        var logicalOffset = 0;

        foreach (var row in rows)
        {
            if (row.AbsoluteRow == absoluteRow)
            {
                return new TerminalTextAnchor(
                    logicalLine,
                    logicalOffset + GetDisplayWidthBeforeColumn(row.Cells, col));
            }

            logicalOffset += GetDisplayWidth(ExtractLogicalCells(row.Cells, trimTrailingSpaces: !row.Wrapped));
            if (!row.Wrapped)
            {
                logicalLine++;
                logicalOffset = 0;
            }
        }

        return new TerminalTextAnchor(Math.Max(0, logicalLine - 1), logicalOffset);
    }

    private List<ResizeRow> CollectResizeRows()
    {
        var rows = new List<ResizeRow>(_scrollback.Count + Rows);
        for (int i = 0; i < _scrollback.Count; i++)
        {
            var wrapped = i < _scrollbackWrapped.Count && _scrollbackWrapped[i];
            rows.Add(new ResizeRow(_scrollback[i], wrapped, i));
        }

        var lastScreenRow = Math.Max(CursorRow, FindLastNonEmptyScreenRow());
        for (int row = 0; row <= lastScreenRow; row++)
            rows.Add(new ResizeRow(GetLine(row), _rowWrapped[row], _scrollback.Count + row));

        if (rows.Count == 0)
            rows.Add(new ResizeRow(CreateEmptyLine(Cols), wrapped: false, absoluteRow: _scrollback.Count));

        return rows;
    }

    private int FindLastNonEmptyScreenRow()
    {
        for (int row = Rows - 1; row >= 0; row--)
        {
            if (_rowWrapped[row] || !IsLineEmpty(GetLine(row)))
                return row;
        }

        return -1;
    }

    private static bool IsLineEmpty(TerminalCell[] line)
    {
        foreach (var cell in line)
        {
            if (cell.Width == 0)
                continue;
            if (cell.Character != '\0' && cell.Character != ' ')
                return false;
        }

        return true;
    }

    private static List<List<TerminalCell>> BuildLogicalLinesForResize(
        List<ResizeRow> rows,
        int cursorAbsoluteRow,
        int cursorCol,
        out int cursorLogicalLine,
        out int cursorLogicalOffset)
    {
        var logicalLines = new List<List<TerminalCell>>();
        var current = new List<TerminalCell>();
        cursorLogicalLine = 0;
        cursorLogicalOffset = 0;
        var cursorFound = false;

        foreach (var row in rows)
        {
            if (!cursorFound && row.AbsoluteRow == cursorAbsoluteRow)
            {
                cursorLogicalLine = logicalLines.Count;
                cursorLogicalOffset = GetDisplayWidth(current) + GetDisplayWidthBeforeColumn(row.Cells, cursorCol);
                cursorFound = true;
            }

            current.AddRange(ExtractLogicalCells(row.Cells, trimTrailingSpaces: !row.Wrapped));

            if (!row.Wrapped)
            {
                logicalLines.Add(current);
                current = new List<TerminalCell>();
            }
        }

        if (current.Count > 0 || logicalLines.Count == 0)
            logicalLines.Add(current);

        if (!cursorFound)
        {
            cursorLogicalLine = logicalLines.Count - 1;
            cursorLogicalOffset = GetDisplayWidth(logicalLines[cursorLogicalLine]);
        }

        return logicalLines;
    }

    private static List<TerminalCell> ExtractLogicalCells(TerminalCell[] line, bool trimTrailingSpaces)
    {
        var cells = new List<TerminalCell>(line.Length);

        foreach (var source in line)
        {
            if (source.Width == 0)
                continue;

            var cell = source;
            cell.Character = cell.Character == '\0' ? ' ' : cell.Character;
            cell.Width = NormalizeLogicalWidth(cell);
            cell.IsDirty = true;
            cells.Add(cell);
        }

        if (trimTrailingSpaces)
        {
            while (cells.Count > 0 && cells[^1].Character == ' ' && cells[^1].Width == 1)
                cells.RemoveAt(cells.Count - 1);
        }

        return cells;
    }

    private static int GetDisplayWidth(List<TerminalCell> cells)
    {
        var width = 0;
        foreach (var cell in cells)
            width += NormalizeLogicalWidth(cell);
        return width;
    }

    private static int GetDisplayWidthBeforeColumn(TerminalCell[] line, int col)
    {
        var width = 0;
        var end = Math.Clamp(col, 0, line.Length);
        for (int i = 0; i < end; i++)
        {
            if (line[i].Width == 0)
                continue;
            width += NormalizeLogicalWidth(line[i]);
        }

        return width;
    }

    private static int GetColumnForDisplayOffset(TerminalCell[] line, int displayOffset)
    {
        displayOffset = Math.Max(0, displayOffset);

        var width = 0;
        for (int col = 0; col < line.Length; col++)
        {
            var cell = line[col];
            if (cell.Width == 0)
                continue;

            var cellWidth = NormalizeLogicalWidth(cell);
            if (width + cellWidth > displayOffset)
                return col;

            width += cellWidth;
            if (width == displayOffset)
                return Math.Min(line.Length - 1, col + cellWidth);
        }

        return GetLastSelectableColumn(line);
    }

    private static int GetLastSelectableColumn(TerminalCell[] line)
    {
        for (int col = line.Length - 1; col >= 0; col--)
        {
            if (line[col].Width != 0)
                return col;
        }

        return 0;
    }

    private static List<ReflowedRow> ReflowLogicalLines(
        List<List<TerminalCell>> logicalLines,
        int cols,
        int cursorLogicalLine,
        int cursorLogicalOffset,
        out int cursorRow,
        out int cursorCol)
    {
        var rows = new List<ReflowedRow>();
        var capturedCursorRow = 0;
        var capturedCursorCol = 0;
        var cursorSet = false;

        for (int lineIndex = 0; lineIndex < logicalLines.Count; lineIndex++)
        {
            var logicalLine = logicalLines[lineIndex];
            var current = CreateEmptyLine(cols);
            var col = 0;
            var logicalOffset = 0;

            void CaptureCursor()
            {
                if (cursorSet || lineIndex != cursorLogicalLine || cursorLogicalOffset > logicalOffset)
                    return;

                capturedCursorRow = rows.Count;
                capturedCursorCol = Math.Clamp(col, 0, cols - 1);
                cursorSet = true;
            }

            if (logicalLine.Count == 0)
            {
                CaptureCursor();
                rows.Add(new ReflowedRow(current, wrapped: false));
                continue;
            }

            CaptureCursor();
            foreach (var cell in logicalLine)
            {
                var width = Math.Min(NormalizeLogicalWidth(cell), cols);
                if (col > 0 && col + width > cols)
                {
                    rows.Add(new ReflowedRow(current, wrapped: true));
                    current = CreateEmptyLine(cols);
                    col = 0;
                    CaptureCursor();
                }

                PlaceCell(current, col, cell, width);
                col += width;
                logicalOffset += width;
                CaptureCursor();
            }

            rows.Add(new ReflowedRow(current, wrapped: false));
        }

        if (rows.Count == 0)
            rows.Add(new ReflowedRow(CreateEmptyLine(cols), wrapped: false));

        if (!cursorSet)
        {
            capturedCursorRow = rows.Count - 1;
            capturedCursorCol = 0;
        }

        cursorRow = capturedCursorRow;
        cursorCol = capturedCursorCol;
        return rows;
    }

    private static int CalculateFirstScreenRow(int rowCount, int rows, int cursorRow, int oldCursorRow)
    {
        if (rowCount <= rows)
            return 0;

        var desiredCursorRow = Math.Clamp(oldCursorRow, 0, rows - 1);
        return Math.Clamp(cursorRow - desiredCursorRow, 0, rowCount - rows);
    }

    private static int NormalizeLogicalWidth(TerminalCell cell)
    {
        if (cell.Width == 2)
            return 2;

        var width = TerminalWidth.GetWidth(cell.Character);
        return width == 2 ? 2 : 1;
    }

    private static TerminalCell[] CreateEmptyLine(int cols)
    {
        var line = new TerminalCell[cols];
        for (int i = 0; i < cols; i++)
            line[i] = TerminalCell.Empty;
        return line;
    }

    private static void PlaceCell(TerminalCell[] line, int col, TerminalCell source, int width)
    {
        if (col < 0 || col >= line.Length)
            return;

        var cell = source;
        cell.Character = cell.Character == '\0' ? ' ' : cell.Character;
        cell.Width = width;
        cell.IsDirty = true;
        line[col] = cell;

        if (width == 2 && col + 1 < line.Length)
            line[col + 1] = CreateContinuationCell(cell.Attribute);
    }

    private readonly struct ResizeRow
    {
        public ResizeRow(TerminalCell[] cells, bool wrapped, int absoluteRow)
        {
            Cells = cells;
            Wrapped = wrapped;
            AbsoluteRow = absoluteRow;
        }

        public TerminalCell[] Cells { get; }
        public bool Wrapped { get; }
        public int AbsoluteRow { get; }
    }

    private readonly struct ReflowedRow
    {
        public ReflowedRow(TerminalCell[] cells, bool wrapped)
        {
            Cells = cells;
            Wrapped = wrapped;
        }

        public TerminalCell[] Cells { get; }
        public bool Wrapped { get; }
    }

    public string ExportPlainText(int maxScrollbackLines = 20000)
    {
        var lines = new List<string>();

        int scrollbackStart = Math.Max(0, _scrollback.Count - Math.Max(0, maxScrollbackLines));
        for (int i = scrollbackStart; i < _scrollback.Count; i++)
            lines.Add(LineToText(_scrollback[i], Cols));

        for (int row = 0; row < Rows; row++)
            lines.Add(LineToText(GetLine(row), Cols));

        int lastNonEmpty = lines.FindLastIndex(line => !string.IsNullOrWhiteSpace(line));
        if (lastNonEmpty < 0)
            return string.Empty;

        return string.Join(Environment.NewLine, lines.Take(lastNonEmpty + 1));
    }

    /// <summary>
    /// Creates a plain-text snapshot of scrollback and visible rows.
    /// Used for restoring terminal context across app restarts.
    /// </summary>
    public TerminalBufferSnapshot CreateSnapshot(int maxScrollbackLines = 3000)
    {
        var snapshot = new TerminalBufferSnapshot
        {
            Cols = Cols,
            Rows = Rows,
            CursorRow = CursorRow,
            CursorCol = CursorCol,
        };

        int scrollbackStart = Math.Max(0, _scrollback.Count - Math.Max(0, maxScrollbackLines));
        for (int i = scrollbackStart; i < _scrollback.Count; i++)
            snapshot.ScrollbackLines.Add(LineToText(_scrollback[i], Cols));

        for (int row = 0; row < Rows; row++)
            snapshot.ScreenLines.Add(LineToText(GetLine(row), Cols));

        return snapshot;
    }

    /// <summary>
    /// Restores a previously captured plain-text snapshot.
    /// </summary>
    public void RestoreSnapshot(TerminalBufferSnapshot snapshot)
    {
        if (snapshot == null) return;

        _scrollback.Clear();
        _scrollbackWrapped.Clear();
        foreach (var line in snapshot.ScrollbackLines)
        {
            _scrollback.Add(TextToLine(line, Cols));
            _scrollbackWrapped.Add(false);
        }

        Clear();

        int rowCount = Math.Min(Rows, snapshot.ScreenLines.Count);
        for (int row = 0; row < rowCount; row++)
        {
            var text = snapshot.ScreenLines[row];
            var line = TextToLine(text, Cols);
            for (int col = 0; col < Cols; col++)
                _cells[row, col] = line[col];
        }

        CursorRow = Math.Clamp(snapshot.CursorRow, 0, Rows - 1);
        CursorCol = Math.Clamp(snapshot.CursorCol, 0, Cols - 1);
        ResetScrollRegion();
        MarkAllDirty();
        RaiseContentChanged();
    }

    private static TerminalCell CreateContinuationCell(TerminalAttribute attr)
    {
        return new TerminalCell
        {
            Character = '\0',
            Attribute = attr,
            IsDirty = true,
            Width = 0,
        };
    }

    private void ShiftCellsRight(int row, int startCol, int count)
    {
        if (row < 0 || row >= Rows || startCol < 0 || startCol >= Cols || count <= 0)
            return;

        for (int col = Cols - 1; col >= startCol + count; col--)
            _cells[row, col] = _cells[row, col - count];

        for (int col = startCol; col < Math.Min(Cols, startCol + count); col++)
            _cells[row, col] = TerminalCell.Empty;

        NormalizeWideCells(row);
    }

    private void ClearCellForWrite(int row, int col)
    {
        if (row < 0 || row >= Rows || col < 0 || col >= Cols)
            return;

        if (_cells[row, col].Width == 0 && col > 0 && _cells[row, col - 1].Width == 2)
            _cells[row, col - 1] = TerminalCell.Empty;
        else if (_cells[row, col].Width == 2 && col + 1 < Cols && _cells[row, col + 1].Width == 0)
            _cells[row, col + 1] = TerminalCell.Empty;

        _cells[row, col] = TerminalCell.Empty;
    }

    private void ClearRange(int row, int startCol, int endCol)
    {
        if (row < 0 || row >= Rows || Cols <= 0)
            return;

        startCol = Math.Clamp(startCol, 0, Cols - 1);
        endCol = Math.Clamp(endCol, 0, Cols - 1);
        if (endCol < startCol)
            return;

        if (startCol > 0 && _cells[row, startCol].Width == 0 && _cells[row, startCol - 1].Width == 2)
            startCol--;
        if (endCol + 1 < Cols && _cells[row, endCol].Width == 2 && _cells[row, endCol + 1].Width == 0)
            endCol++;

        for (int col = startCol; col <= endCol; col++)
            _cells[row, col] = TerminalCell.Empty;
    }

    private void NormalizeWideCells(int row)
    {
        if (row < 0 || row >= Rows)
            return;

        for (int col = 0; col < Cols; col++)
        {
            var cell = _cells[row, col];

            if (cell.Width == 0)
            {
                if (col == 0 || _cells[row, col - 1].Width != 2)
                    _cells[row, col] = TerminalCell.Empty;
                continue;
            }

            if (cell.Width != 2)
            {
                if (cell.Width != 1)
                    _cells[row, col] = TerminalCell.Empty;
                continue;
            }

            if (col + 1 >= Cols)
            {
                _cells[row, col] = TerminalCell.Empty;
                continue;
            }

            _cells[row, col + 1] = CreateContinuationCell(cell.Attribute);
            col++;
        }
    }

    private bool ClampCursorToBounds()
    {
        if (Rows <= 0 || Cols <= 0)
            return false;

        CursorRow = Math.Clamp(CursorRow, 0, Rows - 1);
        CursorCol = Math.Clamp(CursorCol, 0, Cols - 1);
        return true;
    }

    private static string LineToText(TerminalCell[] line, int cols)
    {
        var chars = new List<char>(cols);
        for (int i = 0; i < cols; i++)
        {
            var cell = i < line.Length ? line[i] : TerminalCell.Empty;
            if (cell.Width == 0)
                continue;

            var ch = cell.Character;
            chars.Add(ch == '\0' ? ' ' : ch);
        }

        return new string(chars.ToArray()).TrimEnd();
    }

    private static TerminalCell[] TextToLine(string? text, int cols)
    {
        var line = new TerminalCell[cols];
        for (int i = 0; i < cols; i++)
            line[i] = TerminalCell.Empty;

        if (string.IsNullOrEmpty(text)) return line;

        int col = 0;
        foreach (var ch in text)
        {
            var displayWidth = TerminalWidth.GetWidth(ch);
            if (displayWidth <= 0)
                continue;
            if (col >= cols)
                break;
            var width = Math.Min(displayWidth, cols - col);
            if (width <= 0)
                break;

            line[col] = new TerminalCell
            {
                Character = ch,
                Attribute = TerminalAttribute.Default,
                IsDirty = true,
                Width = width,
            };
            if (width == 2)
                line[col + 1] = CreateContinuationCell(TerminalAttribute.Default);

            col += width;
        }

        return line;
    }

    /// <summary>
    /// Marks all cells as dirty (for full repaint).
    /// </summary>
    public void MarkAllDirty()
    {
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                _cells[r, c].IsDirty = true;
    }

    /// <summary>
    /// Clears dirty flags on all cells.
    /// </summary>
    public void ClearDirty()
    {
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                _cells[r, c].IsDirty = false;
    }

    private void RaiseContentChanged() => ContentChanged?.Invoke();
}

public class TerminalBufferSnapshot
{
    public int Cols { get; set; }
    public int Rows { get; set; }
    public int CursorRow { get; set; }
    public int CursorCol { get; set; }
    public List<string> ScrollbackLines { get; set; } = [];
    public List<string> ScreenLines { get; set; } = [];
}
