using System;
using System.Collections.Generic;
using System.Text;

namespace ClaudeCodeMDI.Terminal;

public class TerminalBuffer
{
    private TerminalCell[,] _cells;
    private readonly List<TerminalCell[]> _scrollback = new();
    private readonly int _maxScrollback;

    public int Rows { get; private set; }
    public int Cols { get; private set; }
    public int CursorRow { get; set; }
    public int CursorCol { get; set; }
    public bool CursorVisible { get; set; } = true;

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

    public void SetCell(int row, int col, TerminalCell cell)
    {
        if (row >= 0 && row < Rows && col >= 0 && col < Cols)
            _cells[row, col] = cell;
    }

    public void WriteChar(char c)
    {
        bool wide = IsWideChar(c);

        if (wide && CursorCol >= Cols - 1)
        {
            // Not enough room for a 2-cell char on this line; wrap
            if (CursorCol < Cols)
                _cells[CursorRow, CursorCol] = TerminalCell.Empty;
            CursorCol = 0;
            LineFeed();
        }
        else if (CursorCol >= Cols)
        {
            CursorCol = 0;
            LineFeed();
        }

        // If we're overwriting a wide-char trail, clear the lead cell too
        if (CursorCol > 0 && _cells[CursorRow, CursorCol].Attributes.HasFlag(CellAttributes.WideCharTrail))
        {
            _cells[CursorRow, CursorCol - 1] = TerminalCell.Empty;
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
            // Write trail marker in the next cell
            _cells[CursorRow, CursorCol] = new TerminalCell
            {
                Character = '\0',
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
    public static bool IsWideChar(char c)
    {
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
                if (_scrollback.Count > _maxScrollback)
                    _scrollback.RemoveAt(0);
            }

            // Shift lines up within scroll region
            for (int r = ScrollTop; r < ScrollBottom; r++)
            {
                for (int c = 0; c < Cols; c++)
                    _cells[r, c] = _cells[r + 1, c];
            }

            // Clear bottom line
            ClearLine(ScrollBottom);
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
            }
            ClearLine(ScrollTop);
        }
    }

    public void ClearLine(int row)
    {
        for (int c = 0; c < Cols; c++)
            _cells[row, c] = TerminalCell.Empty;
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
                if (mode == 3) _scrollback.Clear();
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
                for (int c = 0; c < Cols; c++)
                    _cells[r, c] = _cells[r - 1, c];
            ClearLine(CursorRow);
        }
    }

    public void DeleteLines(int count)
    {
        for (int i = 0; i < count; i++)
        {
            for (int r = CursorRow; r < ScrollBottom; r++)
                for (int c = 0; c < Cols; c++)
                    _cells[r, c] = _cells[r + 1, c];
            ClearLine(ScrollBottom);
        }
    }

    public void DeleteChars(int count)
    {
        for (int i = 0; i < count; i++)
        {
            for (int c = CursorCol; c < Cols - 1; c++)
                _cells[CursorRow, c] = _cells[CursorRow, c + 1];
            _cells[CursorRow, Cols - 1] = TerminalCell.Empty;
        }
    }

    public void InsertChars(int count)
    {
        for (int i = 0; i < count; i++)
        {
            for (int c = Cols - 1; c > CursorCol; c--)
                _cells[CursorRow, c] = _cells[CursorRow, c - 1];
            _cells[CursorRow, CursorCol] = TerminalCell.Empty;
        }
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
        var newCells = new TerminalCell[newRows, newCols];
        int copyRows = Math.Min(Rows, newRows);
        int copyCols = Math.Min(Cols, newCols);
        for (int r = 0; r < copyRows; r++)
            for (int c = 0; c < copyCols; c++)
                newCells[r, c] = _cells[r, c];
        // Fill new cells with empty
        for (int r = 0; r < newRows; r++)
            for (int c = copyCols; c < newCols; c++)
                newCells[r, c] = TerminalCell.Empty;
        for (int r = copyRows; r < newRows; r++)
            for (int c = 0; c < newCols; c++)
                newCells[r, c] = TerminalCell.Empty;

        _cells = newCells;
        Rows = newRows;
        Cols = newCols;
        CursorRow = Math.Clamp(CursorRow, 0, newRows - 1);
        CursorCol = Math.Clamp(CursorCol, 0, newCols - 1);
        ScrollTop = 0;
        ScrollBottom = newRows - 1;
    }

    public void NotifyChanged() => BufferChanged?.Invoke();
}
