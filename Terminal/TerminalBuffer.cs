using System;
using System.Collections.Generic;
using System.Text;

namespace Claucraft.Terminal;

public class TerminalBuffer
{
    private TerminalCell[,] _cells;
    private bool[] _lineWrapped;
    private readonly List<TerminalCell[]> _scrollback = new();
    private readonly List<bool> _scrollbackWrapped = new();
    private readonly int _maxScrollback;

    public int Rows { get; private set; }
    public int Cols { get; private set; }
    public int CursorRow { get; set; }
    public int CursorCol { get; set; }
    public bool CursorVisible { get; set; } = true;
    public bool BracketedPasteMode { get; set; }

    // Current SGR attributes
    public int CurrentFg { get; set; } = -1;
    public int CurrentBg { get; set; } = -1;
    public CellAttributes CurrentAttrs { get; set; } = CellAttributes.None;

    // Scroll region
    public int ScrollTop { get; set; }
    public int ScrollBottom { get; set; }

    // Saved cursor
    private int _savedRow, _savedCol;

    // Scrollback
    public IReadOnlyList<TerminalCell[]> Scrollback => _scrollback;
    public int ScrollOffset { get; set; }

    // Alternate buffer
    private TerminalCell[,]? _altCells;
    private int _altCursorRow, _altCursorCol;
    private bool _isAltBuffer;
    public bool IsAltBuffer => _isAltBuffer;

    public event Action? BufferChanged;

    public TerminalBuffer(int rows, int cols, int maxScrollback = 10000)
    {
        Rows = rows;
        Cols = cols;
        _maxScrollback = maxScrollback;
        _cells = new TerminalCell[rows, cols];
        _lineWrapped = new bool[rows];
        ClearAll();
        ScrollTop = 0;
        ScrollBottom = rows - 1;
    }

    public TerminalCell GetCell(int row, int col)
    {
        if (row >= 0 && row < Rows && col >= 0 && col < Cols)
            return _cells[row, col];
        return TerminalCell.Empty;
    }

    public TerminalCell[]? GetScrollbackLine(int index)
    {
        if (index >= 0 && index < _scrollback.Count)
            return _scrollback[index];
        return null;
    }

    public bool IsLineWrapped(int row)
    {
        if (row >= 0 && row < _lineWrapped.Length)
            return _lineWrapped[row];
        return false;
    }

    public bool IsScrollbackLineWrapped(int index)
    {
        if (index >= 0 && index < _scrollbackWrapped.Count)
            return _scrollbackWrapped[index];
        return false;
    }

    public void SetCell(int row, int col, TerminalCell cell)
    {
        if (row >= 0 && row < Rows && col >= 0 && col < Cols)
            _cells[row, col] = cell;
    }

    /// <summary>Writes one Unicode code point (not a UTF-16 code unit) at the cursor.</summary>
    public void WriteChar(int c)
    {
        // Combining marks, variation selectors and emoji modifiers take no column of their
        // own. The CLI lays its output out that way, so consuming a cell here would shift
        // everything after them to the right. Dropping them keeps the grid aligned.
        if (IsZeroWidth(c)) return;

        bool wide = IsWideChar(c);

        if (wide && CursorCol >= Cols - 1)
        {
            // Not enough room for a 2-cell char on this line; wrap
            if (CursorCol < Cols)
                _cells[CursorRow, CursorCol] = TerminalCell.Empty;
            _lineWrapped[CursorRow] = true;
            CursorCol = 0;
            LineFeed();
        }
        else if (CursorCol >= Cols)
        {
            _lineWrapped[CursorRow] = true;
            CursorCol = 0;
            LineFeed();
        }

        // If we're overwriting a wide-char trail, clear the lead cell too
        if (CursorCol > 0 && _cells[CursorRow, CursorCol].Attributes.HasFlag(CellAttributes.WideCharTrail))
        {
            _cells[CursorRow, CursorCol - 1] = TerminalCell.Empty;
        }

        // If we're overwriting a wide-char lead, clear the orphaned trail cell
        if (CursorCol + 1 < Cols && _cells[CursorRow, CursorCol + 1].Attributes.HasFlag(CellAttributes.WideCharTrail))
        {
            _cells[CursorRow, CursorCol + 1] = TerminalCell.Empty;
        }

        _cells[CursorRow, CursorCol] = new TerminalCell
        {
            Character = c,
            Foreground = CurrentFg,
            Background = CurrentBg,
            Attributes = CurrentAttrs
        };
        CursorCol++;

        if (wide && CursorCol < Cols)
        {
            // If trail position overwrites a wide-char lead, clear its orphaned trail
            if (CursorCol + 1 < Cols && _cells[CursorRow, CursorCol + 1].Attributes.HasFlag(CellAttributes.WideCharTrail))
            {
                _cells[CursorRow, CursorCol + 1] = TerminalCell.Empty;
            }

            // Write trail marker in the next cell
            _cells[CursorRow, CursorCol] = new TerminalCell
            {
                Character = 0,
                Foreground = CurrentFg,
                Background = CurrentBg,
                Attributes = CurrentAttrs | CellAttributes.WideCharTrail
            };
            CursorCol++;
        }
    }

    /// <summary>
    /// Determines if a character is a double-width (full-width) character
    /// that occupies 2 cells in a terminal.
    /// </summary>
    public static bool IsWideChar(int c)
    {
        if (c < 0x1100) return false;

        if (c >= 0x1100 && c <= 0x115F) return true; // Hangul Jamo initial consonants
        // Symbols Unicode gives East_Asian_Width=Wide. The CLI counts these as two
        // columns when it lays out its own output, so the grid has to agree.
        if (c >= 0x231A && c <= 0x231B) return true;
        if (c >= 0x23E9 && c <= 0x23EC) return true;
        if (c == 0x23F0 || c == 0x23F3) return true;
        if (c >= 0x25FD && c <= 0x25FE) return true;
        if (c >= 0x2614 && c <= 0x2615) return true;
        if (c >= 0x2648 && c <= 0x2653) return true;
        if (c == 0x267F || c == 0x2693 || c == 0x26A1) return true;
        if (c >= 0x26AA && c <= 0x26AB) return true;
        if (c >= 0x26BD && c <= 0x26BE) return true;
        if (c >= 0x26C4 && c <= 0x26C5) return true;
        if (c == 0x26CE || c == 0x26D4 || c == 0x26EA) return true;
        if (c >= 0x26F2 && c <= 0x26F3) return true;
        if (c == 0x26F5 || c == 0x26FA || c == 0x26FD) return true;
        if (c == 0x2705) return true;
        if (c >= 0x270A && c <= 0x270B) return true;
        if (c == 0x2728 || c == 0x274C || c == 0x274E) return true;
        if (c >= 0x2753 && c <= 0x2755) return true;
        if (c == 0x2757) return true;
        if (c >= 0x2795 && c <= 0x2797) return true;
        if (c == 0x27B0 || c == 0x27BF) return true;
        if (c >= 0x2B1B && c <= 0x2B1C) return true;
        if (c == 0x2B50 || c == 0x2B55) return true;
        // CJK Radicals Supplement, Kangxi Radicals
        if (c >= 0x2E80 && c <= 0x2FDF) return true;
        // CJK Symbols and Punctuation, Hiragana, Katakana, Bopomofo, etc.
        if (c >= 0x2FF0 && c <= 0x303F) return true;
        if (c >= 0x3040 && c <= 0x309F) return true; // Hiragana
        if (c >= 0x30A0 && c <= 0x30FF) return true; // Katakana
        if (c >= 0x3100 && c <= 0x312F) return true; // Bopomofo
        if (c >= 0x3130 && c <= 0x318F) return true; // Hangul Compatibility Jamo
        if (c >= 0x3190 && c <= 0x31FF) return true; // Kanbun, CJK Strokes
        if (c >= 0x3200 && c <= 0x33FF) return true; // Enclosed CJK, CJK Compatibility
        if (c >= 0x3400 && c <= 0x4DBF) return true; // CJK Unified Ext A
        if (c >= 0x4E00 && c <= 0x9FFF) return true; // CJK Unified Ideographs
        if (c >= 0xA000 && c <= 0xA4CF) return true; // Yi
        if (c >= 0xAC00 && c <= 0xD7AF) return true; // Hangul Syllables
        if (c >= 0xF900 && c <= 0xFAFF) return true; // CJK Compatibility Ideographs
        if (c >= 0xFE10 && c <= 0xFE6F) return true; // CJK Compatibility Forms, Small Forms
        if (c >= 0xFF01 && c <= 0xFF60) return true; // Fullwidth Forms
        if (c >= 0xFFE0 && c <= 0xFFE6) return true; // Fullwidth Signs
        // Above the BMP: emoji and the later CJK extensions.
        if (c >= 0x1F000 && c <= 0x1F02F) return true; // Mahjong Tiles
        if (c >= 0x1F0A0 && c <= 0x1F0FF) return true; // Playing Cards
        if (c >= 0x1F1E6 && c <= 0x1F1FF) return true; // Regional Indicators (flags)
        if (c >= 0x1F200 && c <= 0x1F2FF) return true; // Enclosed Ideographic Supplement
        if (c >= 0x1F300 && c <= 0x1F9FF) return true; // Pictographs, Emoticons, Transport, Supplemental
        if (c >= 0x1FA70 && c <= 0x1FAFF) return true; // Symbols and Pictographs Extended-A
        if (c >= 0x20000 && c <= 0x2FFFD) return true; // CJK Unified Ext B-F
        if (c >= 0x30000 && c <= 0x3FFFD) return true; // CJK Unified Ext G and later
        return false;
    }

    /// <summary>
    /// Characters that attach to the preceding cell instead of taking a column of their own.
    /// </summary>
    public static bool IsZeroWidth(int c)
    {
        if (c < 0x0300) return false;
        if (c >= 0x0300 && c <= 0x036F) return true;   // Combining Diacritical Marks
        if (c >= 0x200B && c <= 0x200F) return true;   // zero-width space/joiners, bidi marks
        if (c == 0xFEFF) return true;                  // zero-width no-break space / BOM
        // U+FE00-FE0F (variation selectors) are deliberately NOT listed. They render as
        // nothing but keep their column, which is how "base + VS16" ends up two columns
        // wide - the same width the CLI counts for an emoji-presentation sequence.
        if (c >= 0x1F3FB && c <= 0x1F3FF) return true; // emoji skin-tone modifiers
        if (c >= 0xE0100 && c <= 0xE01EF) return true; // Variation Selectors Supplement
        return false;
    }

    public void LineFeed()
    {
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

    public void CarriageReturn()
    {
        CursorCol = 0;
    }

    public void Backspace()
    {
        if (CursorCol > 0)
            CursorCol--;
    }

    /// <summary>
    /// Check if the cell at (row, col) is a wide-char trail marker.
    /// </summary>
    public bool IsWideTrail(int row, int col)
    {
        if (row >= 0 && row < Rows && col >= 0 && col < Cols)
            return _cells[row, col].Attributes.HasFlag(CellAttributes.WideCharTrail);
        return false;
    }

    public void Tab()
    {
        int nextTab = ((CursorCol / 8) + 1) * 8;
        CursorCol = Math.Min(nextTab, Cols - 1);
    }

    public void ScrollUp(int lines)
    {
        for (int i = 0; i < lines; i++)
        {
            // Save top line to scrollback (only in main buffer)
            if (!_isAltBuffer && ScrollTop == 0)
            {
                var line = new TerminalCell[Cols];
                for (int c = 0; c < Cols; c++)
                    line[c] = _cells[ScrollTop, c];
                _scrollback.Add(line);
                _scrollbackWrapped.Add(_lineWrapped[ScrollTop]);
                if (_scrollback.Count > _maxScrollback)
                {
                    _scrollback.RemoveAt(0);
                    _scrollbackWrapped.RemoveAt(0);
                }
            }

            // Shift lines up within scroll region
            for (int r = ScrollTop; r < ScrollBottom; r++)
            {
                for (int c = 0; c < Cols; c++)
                    _cells[r, c] = _cells[r + 1, c];
                _lineWrapped[r] = _lineWrapped[r + 1];
            }

            // Clear bottom line
            ClearLine(ScrollBottom);
            _lineWrapped[ScrollBottom] = false;
        }
    }

    public void ScrollDown(int lines)
    {
        for (int i = 0; i < lines; i++)
        {
            for (int r = ScrollBottom; r > ScrollTop; r--)
            {
                for (int c = 0; c < Cols; c++)
                    _cells[r, c] = _cells[r - 1, c];
                _lineWrapped[r] = _lineWrapped[r - 1];
            }
            ClearLine(ScrollTop);
            _lineWrapped[ScrollTop] = false;
        }
    }

    public void ClearLine(int row)
    {
        for (int c = 0; c < Cols; c++)
            _cells[row, c] = TerminalCell.Empty;
        if (row >= 0 && row < _lineWrapped.Length)
            _lineWrapped[row] = false;
    }

    public void ClearAll()
    {
        for (int r = 0; r < Rows; r++)
            ClearLine(r);
    }

    public void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0: // cursor to end
                EraseInLine(0);
                for (int r = CursorRow + 1; r < Rows; r++)
                    ClearLine(r);
                break;
            case 1: // start to cursor
                for (int r = 0; r < CursorRow; r++)
                    ClearLine(r);
                for (int c = 0; c <= CursorCol && c < Cols; c++)
                    _cells[CursorRow, c] = TerminalCell.Empty;
                break;
            case 2: // entire display
            case 3: // entire display + scrollback
                ClearAll();
                if (mode == 3) { _scrollback.Clear(); _scrollbackWrapped.Clear(); }
                break;
        }
    }

    public void EraseInLine(int mode)
    {
        switch (mode)
        {
            case 0: // cursor to end
                for (int c = CursorCol; c < Cols; c++)
                    _cells[CursorRow, c] = TerminalCell.Empty;
                _lineWrapped[CursorRow] = false;
                break;
            case 1: // start to cursor
                for (int c = 0; c <= CursorCol && c < Cols; c++)
                    _cells[CursorRow, c] = TerminalCell.Empty;
                break;
            case 2: // entire line
                ClearLine(CursorRow);
                break;
        }
    }

    public void InsertLines(int count)
    {
        for (int i = 0; i < count; i++)
        {
            for (int r = ScrollBottom; r > CursorRow; r--)
            {
                for (int c = 0; c < Cols; c++)
                    _cells[r, c] = _cells[r - 1, c];
                _lineWrapped[r] = _lineWrapped[r - 1];
            }
            ClearLine(CursorRow);
        }
    }

    public void DeleteLines(int count)
    {
        for (int i = 0; i < count; i++)
        {
            for (int r = CursorRow; r < ScrollBottom; r++)
            {
                for (int c = 0; c < Cols; c++)
                    _cells[r, c] = _cells[r + 1, c];
                _lineWrapped[r] = _lineWrapped[r + 1];
            }
            ClearLine(ScrollBottom);
        }
    }

    public void DeleteChars(int count)
    {
        int start = CursorCol;

        // If cursor is on a wide-char trail, include the lead cell
        if (start > 0 && _cells[CursorRow, start].Attributes.HasFlag(CellAttributes.WideCharTrail))
            start--;

        // Expand count to cover any wide-char pair split at the boundary
        int end = Math.Min(start + count, Cols);
        if (end < Cols && _cells[CursorRow, end].Attributes.HasFlag(CellAttributes.WideCharTrail))
            end++;

        int deleteLen = end - start;

        // Shift cells left
        for (int c = start; c < Cols; c++)
        {
            _cells[CursorRow, c] = (c + deleteLen < Cols)
                ? _cells[CursorRow, c + deleteLen]
                : TerminalCell.Empty;
        }
    }

    public void InsertChars(int count)
    {
        int start = CursorCol;

        // If cursor is on a wide-char trail, include the lead cell
        if (start > 0 && _cells[CursorRow, start].Attributes.HasFlag(CellAttributes.WideCharTrail))
            start--;

        // Shift cells right
        for (int c = Cols - 1; c >= start + count; c--)
            _cells[CursorRow, c] = _cells[CursorRow, c - count];

        // Clear inserted area
        for (int c = start; c < Math.Min(start + count, Cols); c++)
            _cells[CursorRow, c] = TerminalCell.Empty;

        // If a wide-char pair was split at the right edge, clear the orphaned lead
        int boundary = start + count;
        if (boundary < Cols && _cells[CursorRow, boundary].Attributes.HasFlag(CellAttributes.WideCharTrail))
            _cells[CursorRow, boundary] = TerminalCell.Empty;
    }

    public void EraseChars(int count)
    {
        for (int c = CursorCol; c < Math.Min(CursorCol + count, Cols); c++)
            _cells[CursorRow, c] = TerminalCell.Empty;
    }

    public void SaveCursor()
    {
        _savedRow = CursorRow;
        _savedCol = CursorCol;
    }

    public void RestoreCursor()
    {
        CursorRow = Math.Clamp(_savedRow, 0, Rows - 1);
        CursorCol = Math.Clamp(_savedCol, 0, Cols - 1);
    }

    public void SetScrollRegion(int top, int bottom)
    {
        ScrollTop = Math.Clamp(top, 0, Rows - 1);
        ScrollBottom = Math.Clamp(bottom, 0, Rows - 1);
        if (ScrollTop > ScrollBottom)
            (ScrollTop, ScrollBottom) = (ScrollBottom, ScrollTop);
    }

    public void SwitchToAltBuffer()
    {
        if (_isAltBuffer) return;
        _altCells = _cells;
        _altCursorRow = CursorRow;
        _altCursorCol = CursorCol;
        _cells = new TerminalCell[Rows, Cols];
        ClearAll();
        _isAltBuffer = true;
        ScrollTop = 0;
        ScrollBottom = Rows - 1;
    }

    public void SwitchToMainBuffer()
    {
        if (!_isAltBuffer) return;
        _cells = _altCells ?? new TerminalCell[Rows, Cols];
        CursorRow = _altCursorRow;
        CursorCol = _altCursorCol;
        _altCells = null;
        _isAltBuffer = false;
        ScrollTop = 0;
        ScrollBottom = Rows - 1;
    }

    public void Resize(int newRows, int newCols)
    {
        var newCells = ResizeGrid(_cells, newRows, newCols);

        // The saved main screen has to travel with the resize. Left at its old size it still
        // gets installed by SwitchToMainBuffer when the CLI leaves the alternate screen, and
        // from then on Rows and Cols describe a grid that no longer exists: every bounds check
        // in this class passes and the next read runs off the end of the array. That is what
        // takes the whole app down when one of two MDI windows is closed and the survivor is
        // re-tiled while its CLI is on the alternate screen.
        if (_altCells != null)
            _altCells = ResizeGrid(_altCells, newRows, newCols);

        var newWrapped = new bool[newRows];
        for (int r = 0; r < Math.Min(_lineWrapped.Length, newRows); r++)
            newWrapped[r] = _lineWrapped[r];
        _lineWrapped = newWrapped;

        _cells = newCells;
        Rows = newRows;
        Cols = newCols;
        CursorRow = Math.Clamp(CursorRow, 0, newRows - 1);
        CursorCol = Math.Clamp(CursorCol, 0, newCols - 1);
        ScrollTop = 0;
        ScrollBottom = newRows - 1;
    }

    /// <summary>
    /// Copies a cell grid into one of the requested size, keeping the top-left overlap. Sized
    /// off the source array itself rather than <see cref="Rows"/> and <see cref="Cols"/>, so it
    /// is correct for the saved alternate-screen grid too - that one is not always the same
    /// shape as the live one.
    /// </summary>
    private static TerminalCell[,] ResizeGrid(TerminalCell[,] cells, int newRows, int newCols)
    {
        var grid = new TerminalCell[newRows, newCols];
        int copyRows = Math.Min(cells.GetLength(0), newRows);
        int copyCols = Math.Min(cells.GetLength(1), newCols);

        for (int r = 0; r < copyRows; r++)
            for (int c = 0; c < copyCols; c++)
                grid[r, c] = cells[r, c];

        // Everything outside the overlap is blank, not default(TerminalCell) - the default has
        // colour index 0 rather than "use the terminal default".
        for (int r = 0; r < copyRows; r++)
            for (int c = copyCols; c < newCols; c++)
                grid[r, c] = TerminalCell.Empty;
        for (int r = copyRows; r < newRows; r++)
            for (int c = 0; c < newCols; c++)
                grid[r, c] = TerminalCell.Empty;

        return grid;
    }

    public void NotifyChanged() => BufferChanged?.Invoke();
}
