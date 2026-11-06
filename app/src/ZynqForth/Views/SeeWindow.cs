using System.Windows;
using System.Windows.Controls;
using ZynqForth.Board;

namespace ZynqForth.Views;

/// <summary>
/// Shows a decompiled word: its Forth source, or with "Listing" every cell with its address,
/// instruction and meaning. "To editor" appends the source to the editor.
/// </summary>
public sealed class SeeWindow : Window
{
    public SeeWindow(Decompiled word, Window owner, Action<string> toEditor)
    {
        Owner = owner;
        Title = $"See: {word.Name}   (${word.CodeAddress:X4}..${word.End - 1:X4}, {(word.End - word.CodeAddress) / 2} cells)";
        Width = 680;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Zf.Window");

        var text = new TextBox
        {
            Text = word.Source,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(8, 8, 8, 4),
        };
        text.SetResourceReference(FontFamilyProperty, "Zf.CodeFont");
        text.SetResourceReference(FontSizeProperty, "Zf.CodeSize");
        text.SetResourceReference(BackgroundProperty, "Zf.EditorBackground");
        text.SetResourceReference(ForegroundProperty, "Zf.EditorText");

        var listing = new CheckBox { Content = "Listing", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        listing.SetResourceReference(ForegroundProperty, "Zf.Text");
        listing.Click += (_, _) => text.Text = listing.IsChecked == true ? word.Listing : word.Source;

        Button Make(string caption, string tip, Action click)
        {
            var b = new Button { Content = caption, ToolTip = tip, Margin = new Thickness(6, 0, 0, 0), MinWidth = 80 };
            b.Click += (_, _) => click();
            return b;
        }

        var note = new TextBlock
        {
            Text = "Comments and stack comments are not stored on the board.",
            VerticalAlignment = VerticalAlignment.Center,
        };
        note.SetResourceReference(ForegroundProperty, "Zf.DimText");

        var bar = new DockPanel { Margin = new Thickness(8, 4, 8, 8), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(listing);
        buttons.Children.Add(Make("Copy", "Copy the text to the clipboard", () => Clipboard.SetText(text.Text)));
        buttons.Children.Add(Make("To editor", "Append the source to the editor", () => toEditor(word.Source)));
        buttons.Children.Add(Make("Close", "Close this window", Close));
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(note);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(text);
        Content = root;
    }
}
