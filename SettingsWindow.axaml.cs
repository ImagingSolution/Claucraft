using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace ClaudeCodeMDI;

public partial class SettingsWindow : Window
{
    private static readonly List<string> FontList = new()
    {
        // Monospace
        "Cascadia Mono",
        "Cascadia Code",
        "Consolas",
        "Courier New",
        "Source Code Pro",
        "JetBrains Mono",
        "Fira Code",
        "Hack",
        "DejaVu Sans Mono",
        "Lucida Console",
        // Proportional
        "Segoe UI",
        "Arial",
        "Verdana",
        "Tahoma",
        "Calibri",
        // Japanese
        "MS Gothic",
        "BIZ UDGothic",
        "Yu Gothic",
        "Yu Gothic UI",
        "Meiryo",
        "Meiryo UI",
        "BIZ UDMincho",
        "MS Mincho",
    };

    public string SelectedFontFamily { get; private set; } = "Cascadia Mono";
    public double SelectedFontSize { get; private set; } = 14;
    public bool Confirmed { get; private set; }

    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(string currentFont, double currentSize) : this()
    {
        SelectedFontFamily = currentFont;
        SelectedFontSize = currentSize;

        var availableFonts = new List<string>();
        foreach (var name in FontList)
        {
            try
            {
                var tf = new Typeface(name);
                if (tf.GlyphTypeface != null)
                    availableFonts.Add(name);
            }
            catch { }
        }
        if (availableFonts.Count == 0)
            availableFonts.AddRange(FontList);

        CmbFontFamily.ItemsSource = availableFonts;
        CmbFontFamily.SelectedItem = availableFonts.Contains(currentFont) ? currentFont : availableFonts[0];
        NumFontSize.Value = (decimal)currentSize;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        SelectedFontFamily = CmbFontFamily.SelectedItem as string ?? "Cascadia Mono";
        SelectedFontSize = (double)(NumFontSize.Value ?? 14);
        Confirmed = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
