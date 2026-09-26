using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyApp.Models;

namespace MyApp;

public sealed partial class FormattingToolbar : UserControl
{
    public FormattingToolbar()
    {
        InitializeComponent();
    }

    public void SetMode(EditorMode mode)
    {
        bool rich = mode == EditorMode.RichText;
        bool markdown = mode == EditorMode.Markdown;
        Visibility formatting = rich || markdown ? Visibility.Visible : Visibility.Collapsed;

        BoldButton.Visibility = formatting;
        ItalicButton.Visibility = formatting;
        StrikethroughButton.Visibility = formatting;
        ListButton.Visibility = formatting;
        UnderlineButton.Visibility = rich ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetState(bool bold, bool italic, bool underline, bool strikethrough, bool list)
    {
        BoldButton.IsChecked = bold;
        ItalicButton.IsChecked = italic;
        UnderlineButton.IsChecked = underline;
        StrikethroughButton.IsChecked = strikethrough;
        ListButton.IsChecked = list;
    }

    public void SetListStyle(MarkerType style)
    {
        bool numbered = style == MarkerType.Arabic;
        BulletListIcon.Visibility = numbered ? Visibility.Collapsed : Visibility.Visible;
        NumberedListIcon.Visibility = numbered ? Visibility.Visible : Visibility.Collapsed;
        NumberedListItem.IsChecked = numbered;
        BulletListItem.IsChecked = !numbered;
    }

    private static MainPage? Page => MainPage.Current;

    private void BoldButton_Click(object sender, RoutedEventArgs e) => Page?.ToggleBold();

    private void ItalicButton_Click(object sender, RoutedEventArgs e) => Page?.ToggleItalic();

    private void UnderlineButton_Click(object sender, RoutedEventArgs e) => Page?.ToggleUnderline();

    private void StrikethroughButton_Click(object sender, RoutedEventArgs e) => Page?.ToggleStrikethrough();

    private void ListButton_Click(object sender, RoutedEventArgs e) => Page?.ToggleList();

    private void ListStyleItem_Click(object sender, RoutedEventArgs e) =>
        Page?.SetListStyle(ReferenceEquals(sender, NumberedListItem) ? MarkerType.Arabic : MarkerType.Bullet);
}
