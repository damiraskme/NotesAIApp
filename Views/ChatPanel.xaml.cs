using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MyApp.ViewModels;
using System.ComponentModel;
using Windows.System;

namespace MyApp;

public sealed partial class ChatPanel : UserControl
{
    public ChatViewModel Chat { get; }

    public event EventHandler? CloseRequested;

    public ChatPanel()
    {
        Chat = MainWindow.Current.ViewModel.Chat;
        InitializeComponent();

        ProviderBox.ItemsSource = Chat.Providers.Select(p => p.DisplayName).ToList();
        SyncProvider();
        Chat.PropertyChanged += Chat_PropertyChanged;
        Chat.Messages.CollectionChanged += (s, e) => DispatcherQueue.TryEnqueue(ScrollToEnd);
    }

    public void FocusPrompt() => DispatcherQueue.TryEnqueue(() => PromptBox.Focus(FocusState.Programmatic));

    private async Task SendAsync()
    {
        string prompt = PromptBox.Text.Trim();
        if (prompt.Length == 0 || Chat.IsBusy) return;
        if (!await EnsureNoteSavedAsync()) return;

        PromptBox.Text = string.Empty;
        await Chat.SendAsync(prompt);
        PromptBox.Focus(FocusState.Programmatic);
    }

    private async Task<bool> EnsureNoteSavedAsync()
    {
        if (MainWindow.Current.ViewModel.ActiveTab is not { FilePath: null }) return true;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Save this note?",
            Content = "AI chats are stored together with the note. Save the note first so this conversation is kept.",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Send without saving",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ActualTheme,
        };

        bool send = await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => MainPage.Current is MainPage page && await page.SaveAsAsync(),
            ContentDialogResult.Secondary => true,
            _ => false,
        };
        FocusPrompt();
        return send;
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private async void PromptBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await SendAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void Chat_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.SelectedProvider)) SyncProvider();
        if (e.PropertyName == nameof(ChatViewModel.IsBusy)) SendButton.IsEnabled = !Chat.IsBusy;
    }

    private void SyncProvider()
    {
        int index = Chat.Providers.ToList().IndexOf(Chat.SelectedProvider);
        if (ProviderBox.SelectedIndex != index) ProviderBox.SelectedIndex = index;
    }

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int index = ProviderBox.SelectedIndex;
        if (index < 0 || index >= Chat.Providers.Count) return;
        if (!ReferenceEquals(Chat.Providers[index], Chat.SelectedProvider)) Chat.SelectedProvider = Chat.Providers[index];
    }

    private void ScrollToEnd()
    {
        MessagesScroller.UpdateLayout();
        MessagesScroller.ChangeView(null, MessagesScroller.ScrollableHeight, null);
    }
}
