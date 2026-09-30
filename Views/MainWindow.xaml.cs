using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MyApp.Models;
using MyApp.Services;
using MyApp.ViewModels;
using System;
using System.ComponentModel;
using System.Diagnostics;
using Windows.System;
using Windows.UI.Core;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace MyApp;

public sealed partial class MainWindow : Window
{
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MINIMIZE = 0xF020;

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    public static MainWindow Current { get; private set; }
    public MainViewModel ViewModel { get; }
    private bool _closeConfirmed;

    private const double SplitterWidth = 5;
    private const double MinChatWidth = 240;
    private const double MinNoteWidth = 280;
    private bool _chatOpen;
    private double _chatDragStartWidth;

    private double Scale => RootGrid.XamlRoot?.RasterizationScale ?? 1.0;

    private void UpdateChatPanel(bool resizeWindow)
    {
        bool open = ViewModel.Settings.Appearance.ShowChatPanel;
        if (open == _chatOpen) return;

        double panelWidth = open
            ? Math.Max(MinChatWidth, ViewModel.Settings.Appearance.ChatPanelWidth)
            : ChatColumn.ActualWidth;
        double total = panelWidth + SplitterWidth;

        _chatOpen = open;
        SplitterColumn.Width = new GridLength(open ? SplitterWidth : 0);
        ChatColumn.Width = new GridLength(open ? panelWidth : 0);
        ChatSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ChatPanelControl.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        if (resizeWindow && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            int delta = (int)Math.Round(total * Scale) * (open ? 1 : -1);
            PointInt32 position = AppWindow.Position;
            SizeInt32 size = AppWindow.Size;
            int width = size.Width + delta;

            RectInt32 work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            width = Math.Min(width, work.Width);
            int x = Math.Max(work.X, Math.Min(position.X, work.X + work.Width - width));
            AppWindow.MoveAndResize(new RectInt32(x, position.Y, width, size.Height));
        }

        if (open) ChatPanelControl.FocusPrompt();
        else Page?.FocusEditor();
    }


    private void ChatSplitter_DragStarted(object? sender, EventArgs e) => _chatDragStartWidth = ChatColumn.ActualWidth;

    private void ChatSplitter_DragDelta(object? sender, double delta)
    {
        double max = Math.Max(MinChatWidth, ContentArea.ActualWidth - MinNoteWidth - SplitterWidth);
        ChatColumn.Width = new GridLength(Math.Clamp(_chatDragStartWidth - delta, MinChatWidth, max));
    }

    private void ChatSplitter_DragCompleted(object? sender, EventArgs e) =>
        ViewModel.Settings.Appearance.ChatPanelWidth = Math.Round(ChatColumn.ActualWidth);
    public MainWindow()
    {
        Current = this;
        ViewModel = new MainViewModel();
        InitializeComponent();

        Title = ProjectPaths.AppName;

        ThemeService.RegisterRoot(RootGrid);
        if (!ThemeService.Apply(ViewModel.Settings.Appearance.Theme))
        {
            ThemeService.Apply(ThemeService.DefaultTheme);
        }

        BuildThemeSettings();
        BuildAiProviderSettings();
        UpdateSaveFolderText();
        SettingsNav.SelectedIndex = 0;
        BuildRecentMenu();
        BuildNewMenus();

        ViewModel.Settings.Appearance.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(Models.Settings.AppearanceSettings.ShowChatPanel)) UpdateChatPanel(resizeWindow: true);
        };
        RootGrid.Loaded += (s, e) => UpdateChatPanel(resizeWindow: true);
        ChatPanelControl.CloseRequested += (s, e) => ViewModel.Settings.Appearance.ShowChatPanel = false;
        ChatSplitter.DragStarted += ChatSplitter_DragStarted;
        ChatSplitter.DragDelta += ChatSplitter_DragDelta;
        ChatSplitter.DragCompleted += ChatSplitter_DragCompleted;
        ViewModel.Storage.State.Session.PropertyChanged += Session_PropertyChanged;
        RootGrid.PreviewKeyDown += RootGrid_PreviewKeyDown;

        ExtendsContentIntoTitleBar = true;

        var presenter = AppWindow.Presenter as OverlappedPresenter;
        if (presenter != null)
        {
            presenter.SetBorderAndTitleBar(true, false);
        }

        DragRegion.SizeChanged += (s, e) => UpdateDragRegion();
        SizeChanged += (s, e) => UpdateDragRegion();
        DragRegion.Loaded += (s, e) => UpdateDragRegion();
        TabStrip.SizeChanged += (s, e) => UpdateDragRegion();

        DragRegion.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler((s, e) => (RootFrame.Content as MainPage)?.FocusEditor()), true);

        TabScroller.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(TabScroller_PointerWheelChanged), true);

        ViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ActiveTab) && ViewModel.ActiveTab is NoteTab tab)
            {
                DispatcherQueue.TryEnqueue(() => BringTabIntoView(tab));
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => BringTabIntoView(tab));
            }
        };

        TabScroller.ViewChanged += (s, e) => UpdateTabArrows();
        TabScroller.SizeChanged += (s, e) => UpdateTabArrows();
        TabItems.SizeChanged += (s, e) => UpdateTabArrows();

        AppWindow.Closing += (s, e) =>
        {
            if (_closeConfirmed) return;
            e.Cancel = true;
            RequestClose();
        };

        AppWindow.Resize(new SizeInt32 {Width = 512, Height = 512});
        AppWindow.SetTaskbarIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        
        RootFrame.Navigate(typeof(MainPage));
    }
    
    private void UpdateDragRegion()
    {
        if (DragRegion.ActualWidth == 0 || DragRegion.ActualHeight == 0 || AutoSaveToggleSwitch.ActualWidth == 0)
            return;

        double scale = DragRegion.XamlRoot?.RasterizationScale ?? 1.0;

        if (IsSettingsOpen)
        {
            SetSettingsDragRegion(scale);
            return;
        }

        var dragRegionTransform = DragRegion.TransformToVisual(null);
        var dragRegionBounds = dragRegionTransform.TransformBounds(
            new Windows.Foundation.Rect(0, 0, DragRegion.ActualWidth, DragRegion.ActualHeight));

        var switchTransform = AutoSaveToggleSwitch.TransformToVisual(null);
        var switchBounds = switchTransform.TransformBounds(
            new Windows.Foundation.Rect(0, 0, AutoSaveToggleSwitch.ActualWidth, AutoSaveToggleSwitch.ActualHeight));

        var tabStripTransform = TabStrip.TransformToVisual(null);
        var tabStripBounds = tabStripTransform.TransformBounds(
            new Windows.Foundation.Rect(0, 0, TabStrip.ActualWidth, TabStrip.ActualHeight));

        var onTopButtonTransform = OnTopButton.TransformToVisual(null);
        var onTopButtonBounds = onTopButtonTransform.TransformBounds(
            new Windows.Foundation.Rect(0, 0, OnTopButton.ActualWidth, OnTopButton.ActualHeight));

        List<RectInt32> dragRects = new List<RectInt32>();

        int rect1Width = (int)((switchBounds.Left - dragRegionBounds.Left) * scale);
        if (rect1Width > 0)
        {
            dragRects.Add(new RectInt32(
                (int)(dragRegionBounds.X * scale),
                (int)(dragRegionBounds.Y * scale),
                rect1Width,
                (int)(dragRegionBounds.Height * scale)));
        }

        int rect2X = (int)(tabStripBounds.Right * scale);
        int rect2Width = (int)((onTopButtonBounds.Left - tabStripBounds.Right) * scale);
        if (rect2Width > 0)
        {
            dragRects.Add(new RectInt32(
                rect2X,
                (int)(dragRegionBounds.Y * scale),
                rect2Width,
                (int)(dragRegionBounds.Height * scale)));
        }

        double controlsHeight = DragRegion.RowDefinitions[0].ActualHeight;
        int bottomStripHeight = (int)((dragRegionBounds.Height - controlsHeight) * scale);
        if (bottomStripHeight > 0)
        {
            dragRects.Add(new RectInt32(
                (int)(dragRegionBounds.X * scale),
                (int)((dragRegionBounds.Y + controlsHeight) * scale),
                (int)(dragRegionBounds.Width * scale),
                bottomStripHeight));
        }

        var nonClientSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        nonClientSource.SetRegionRects(NonClientRegionKind.Caption, dragRects.ToArray());
    }
    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        PostMessage(hwnd, WM_SYSCOMMAND, (IntPtr)SC_MINIMIZE, IntPtr.Zero);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        RequestClose();
    }

    private async void RequestClose()
    {
        if (RootFrame.Content is MainPage mainPage && !await mainPage.ConfirmCloseAllAsync()) return;

        ViewModel.Storage.Flush();
        _closeConfirmed = true;
        Close();
    }
    private void StayOnTop_Click(object sender, RoutedEventArgs e)
    {
        if(AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = !presenter.IsAlwaysOnTop;
            if (sender is Button target)
            {
                target.Content = presenter.IsAlwaysOnTop ? "\uE77A" : "\uE718";
            }
        }
    }
    private void SaveNote_Click(object sender, RoutedEventArgs e)
    {
        if(RootFrame.Content is MainPage mainPage)
        {
            mainPage.SaveNoteContent();
        }
    }

    private void NewFile_Click(object sender, RoutedEventArgs e)
    {
        if (RootFrame.Content is MainPage mainPage)
        {
            mainPage.NewTab();
        }
    }

    private void Tab_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (RootFrame.Content is not MainPage mainPage || sender is not FrameworkElement { DataContext: NoteTab tab }) return;

        var point = e.GetCurrentPoint((UIElement)sender).Properties;
        if (point.IsLeftButtonPressed)
        {
            mainPage.ActivateTab(tab);
            BringTabIntoView(tab);
            mainPage.FocusEditor();
        }
        else if (point.IsMiddleButtonPressed)
        {
            _ = mainPage.CloseTabAsync(tab);
        }
    }

    private async void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (RootFrame.Content is MainPage mainPage && sender is FrameworkElement { DataContext: NoteTab tab })
        {
            await mainPage.CloseTabAsync(tab);
        }
    }

    private void Tab_PointerEntered(object sender, PointerRoutedEventArgs e) => SetTabHover(sender, 1);

    private void Tab_PointerExited(object sender, PointerRoutedEventArgs e) => SetTabHover(sender, 0);

    private static void SetTabHover(object sender, double opacity)
    {
        if (sender is FrameworkElement tab && tab.FindName("HoverBorder") is Border hover)
        {
            hover.Opacity = opacity;
        }
    }

    private void BringTabIntoView(NoteTab tab)
    {
        TabStrip.UpdateLayout();
        UpdateTabArrows();
        TabStrip.UpdateLayout();
        if (TabItems.ContainerFromItem(tab) is not FrameworkElement container || container.ActualWidth == 0) return;

        var bounds = container.TransformToVisual(TabItems).TransformBounds(
            new Windows.Foundation.Rect(0, 0, container.ActualWidth, container.ActualHeight));
        double viewLeft = TabScroller.HorizontalOffset;
        double viewRight = viewLeft + TabScroller.ViewportWidth;

        if (bounds.Left < viewLeft)
        {
            TabScroller.ChangeView(bounds.Left, null, null);
        }
        else if (bounds.Right > viewRight)
        {
            TabScroller.ChangeView(bounds.Right - TabScroller.ViewportWidth, null, null);
        }
    }

    private void ScrollTabsLeft_Click(object sender, RoutedEventArgs e) => ScrollTabs(-1);

    private void ScrollTabsRight_Click(object sender, RoutedEventArgs e) => ScrollTabs(1);

    private void ScrollTabs(int direction)
    {
        double step = TabScroller.ViewportWidth * 0.75 * direction;
        TabScroller.ChangeView(TabScroller.HorizontalOffset + step, null, null);
    }

    private void UpdateTabArrows()
    {
        bool overflows = TabScroller.ScrollableWidth > 0.5;
        var visibility = overflows ? Visibility.Visible : Visibility.Collapsed;
        ScrollTabsLeftButton.Visibility = visibility;
        ScrollTabsRightButton.Visibility = visibility;

        ScrollTabsLeftButton.IsEnabled = TabScroller.HorizontalOffset > 0.5;
        ScrollTabsRightButton.IsEnabled = TabScroller.HorizontalOffset < TabScroller.ScrollableWidth - 0.5;
    }

    private void BuildThemeSettings()
    {
        ThemeRadioButtons.ItemsSource = ThemeService.Themes.Select(t => t.DisplayName).ToList();
        ThemeRadioButtons.SelectedIndex = ThemeService.Themes
            .Select((t, i) => (t, i))
            .FirstOrDefault(x => x.t.Name == ThemeService.CurrentTheme).i;
    }

    private void BuildAiProviderSettings()
    {
        AiProvidersPanel.Children.Clear();
        var textBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["NoteTextBrush"];

        foreach (IAiProvider provider in AiProviders.All)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = provider.DisplayName, Foreground = textBrush });
            info.Children.Add(new TextBlock
            {
                Text = provider.RequiresApiKey ? "No API key" : "No key needed",
                FontSize = 12,
                Opacity = 0.7,
                Foreground = textBrush,
            });
            row.Children.Add(info);

            if (provider.RequiresApiKey)
            {
                var add = new Button { Content = "Add key", VerticalAlignment = VerticalAlignment.Center };
                add.Click += async (s, e) => await PromptForApiKeyAsync(provider);
                Grid.SetColumn(add, 1);
                row.Children.Add(add);
            }

            AiProvidersPanel.Children.Add(row);
        }
    }

    private async Task PromptForApiKeyAsync(IAiProvider provider)
    {
        var keyBox = new PasswordBox
        {
            PlaceholderText = "Paste your API key",
            PasswordRevealMode = PasswordRevealMode.Peek,
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = "Keys are not stored yet. Saving will be connected in a later version.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        });
        content.Children.Add(keyBox);

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = $"{provider.DisplayName} API key",
            Content = content,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            RequestedTheme = RootGrid.ActualTheme,
        };
        keyBox.PasswordChanged += (s, e) => dialog.IsPrimaryButtonEnabled = keyBox.Password.Trim().Length > 0;
        dialog.Opened += (s, e) => keyBox.Focus(FocusState.Programmatic);

        await dialog.ShowAsync();
    }

    private void SettingsNav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        string page = (SettingsNav.SelectedItem as ListViewItem)?.Tag as string ?? "Files";
        FilesPage.Visibility = page == "Files" ? Visibility.Visible : Visibility.Collapsed;
        ThemePage.Visibility = page == "Theme" ? Visibility.Visible : Visibility.Collapsed;
        AiPage.Visibility = page == "Ai" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSaveFolderText()
    {
        string? folder = ViewModel.Settings.Editor.DefaultSaveFolder;
        bool custom = !string.IsNullOrEmpty(folder);
        SaveFolderText.Text = custom ? folder : "Documents";
        ToolTipService.SetToolTip(SaveFolderText, SaveFolderText.Text);
        ResetSaveFolderButton.IsEnabled = custom;
    }

    private async void ChangeSaveFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(AppWindow.Id)
        {
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
        };
        string? current = ViewModel.Settings.Editor.DefaultSaveFolder;
        if (!string.IsNullOrEmpty(current) && Directory.Exists(current)) picker.SuggestedFolder = current;

        var result = await picker.PickSingleFolderAsync();
        if (result is null) return;

        ViewModel.Settings.Editor.DefaultSaveFolder = result.Path;
        UpdateSaveFolderText();
    }

    private void ResetSaveFolder_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Settings.Editor.DefaultSaveFolder = null;
        UpdateSaveFolderText();
    }

    private void ThemeRadioButtons_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int index = ThemeRadioButtons.SelectedIndex;
        if (index < 0 || index >= ThemeService.Themes.Count) return;

        string name = ThemeService.Themes[index].Name;
        if (name == ThemeService.CurrentTheme || !ThemeService.Apply(name)) return;
        ViewModel.Settings.Appearance.Theme = name;
    }

    public bool IsSettingsOpen => SettingsOverlay.Visibility == Visibility.Visible;

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsSettingsOpen) CloseSettings();
        else OpenSettings();
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e) => CloseSettings();

    private void OpenSettings()
    {
        SettingsOverlay.Visibility = Visibility.Visible;
        DispatcherQueue.TryEnqueue(() =>
        {
            CloseSettingsButton.Focus(FocusState.Programmatic);
            UpdateDragRegion();
        });
    }

    private void CloseSettings()
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
        DispatcherQueue.TryEnqueue(UpdateDragRegion);
        Page?.FocusEditor();
    }

    private void SettingsHeader_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateDragRegion();

    private void SetSettingsDragRegion(double scale)
    {
        if (SettingsHeader.ActualWidth == 0) return;

        var header = SettingsHeader.TransformToVisual(null).TransformBounds(
            new Windows.Foundation.Rect(0, 0, SettingsHeader.ActualWidth, SettingsHeader.ActualHeight));
        var close = CloseSettingsButton.TransformToVisual(null).TransformBounds(
            new Windows.Foundation.Rect(0, 0, CloseSettingsButton.ActualWidth, CloseSettingsButton.ActualHeight));

        var caption = new RectInt32(
            (int)(close.Right * scale),
            (int)(header.Y * scale),
            (int)((header.Right - close.Right) * scale),
            (int)(header.Height * scale));
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
            .SetRegionRects(NonClientRegionKind.Caption, new[] { caption });
    }

    private void MenuItem_RefocusEditor(object sender, RoutedEventArgs e)
    {
        if (RootFrame.Content is MainPage mainPage)
        {
            mainPage.FocusEditor();
        }
    }

    private void TabScroller_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(TabScroller).Properties.MouseWheelDelta;
        TabScroller.ChangeView(TabScroller.HorizontalOffset - delta, null, null, true);
        e.Handled = true;
    }

    private MainPage? Page => RootFrame.Content as MainPage;

    public FormattingToolbar FormattingToolbar => FormattingToolbarControl;

    private static readonly (string Text, string Extension, string Shortcut)[] NewTabTypes =
    {
        ("Note (.textpack)", TextPackService.Extension, "Ctrl+1"),
        ("Plain text (.txt)", ".txt", "Ctrl+2"),
        ("Markdown (.md)", ".md", "Ctrl+3"),
        ("Rich text (.rtf)", ".rtf", "Ctrl+4"),
    };

    private readonly MenuFlyout _newFlyout = new();

    private void BuildNewMenus()
    {
        foreach (var (text, extension, shortcut) in NewTabTypes)
        {
            NewMenu.Items.Add(CreateNewTabItem(text, extension, shortcut));
            _newFlyout.Items.Add(CreateNewTabItem(text, extension, shortcut));
        }

        _newFlyout.Opened += (s, e) => (_newFlyout.Items[0] as Control)?.Focus(FocusState.Keyboard);
        _newFlyout.Closed += (s, e) => Page?.FocusEditor();
    }

    private MenuFlyoutItem CreateNewTabItem(string text, string extension, string shortcut)
    {
        var item = new MenuFlyoutItem { Text = text, KeyboardAcceleratorTextOverride = shortcut };
        item.Click += (s, e) => Page?.NewTab(extension);
        item.KeyDown += NewTabItem_KeyDown;
        return item;
    }

    private void NewTabItem_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (NewTabTypeForKey(e.Key) is not string extension) return;

        e.Handled = true;
        OpenNewTab(extension);
    }

    private void OpenNewTab(string extension)
    {
        _newFlyout.Hide();
        foreach (Popup popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(Content.XamlRoot))
        {
            popup.IsOpen = false;
        }
        Page?.NewTab(extension);
        Page?.FocusEditor();
    }

    private static string? NewTabTypeForKey(VirtualKey key) => key switch
    {
        VirtualKey.Number1 or VirtualKey.NumberPad1 => NewTabTypes[0].Extension,
        VirtualKey.Number2 or VirtualKey.NumberPad2 => NewTabTypes[1].Extension,
        VirtualKey.Number3 or VirtualKey.NumberPad3 => NewTabTypes[2].Extension,
        VirtualKey.Number4 or VirtualKey.NumberPad4 => NewTabTypes[3].Extension,
        _ => null,
    };

    private void ShowNewMenu()
    {
        _newFlyout.ShowAt(FileMenu, new FlyoutShowOptions
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            ShowMode = FlyoutShowMode.Standard,
        });
    }

    private void NewWindow_Click(object sender, RoutedEventArgs e) => OpenNewWindow();

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveActiveAsync();

    private async void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        if (Page is MainPage page) await page.SaveAllAsync();
    }

    private async void CloseTab_MenuClick(object sender, RoutedEventArgs e)
    {
        if (Page is MainPage page) await page.CloseActiveTabAsync();
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => RequestClose();

    private void Undo_Click(object sender, RoutedEventArgs e) => Page?.Undo();

    private void Cut_Click(object sender, RoutedEventArgs e) => Page?.Cut();

    private void Copy_Click(object sender, RoutedEventArgs e) => Page?.Copy();

    private void Paste_Click(object sender, RoutedEventArgs e) => Page?.Paste();

    private void Delete_Click(object sender, RoutedEventArgs e) => Page?.Delete();

    private void ClearFormatting_Click(object sender, RoutedEventArgs e) => Page?.ClearFormatting();

    private async void Define_Click(object sender, RoutedEventArgs e)
    {
        if (Page is MainPage page) await page.DefineWithBingAsync();
    }

    private void Find_Click(object sender, RoutedEventArgs e) => Page?.ShowFind(replace: false);

    private void FindNext_Click(object sender, RoutedEventArgs e) => Page?.FindNext();

    private void FindPrevious_Click(object sender, RoutedEventArgs e) => Page?.FindPrevious();

    private void Replace_Click(object sender, RoutedEventArgs e) => Page?.ShowFind(replace: true);

    private async void GoTo_Click(object sender, RoutedEventArgs e)
    {
        if (Page is MainPage page) await page.GoToLineAsync();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => Page?.SelectAll();

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Page?.ZoomIn();

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Page?.ZoomOut();

    private void ResetZoom_Click(object sender, RoutedEventArgs e) => Page?.ResetZoom();

    private async Task SaveActiveAsync()
    {
        if (Page is not MainPage page) return;
        await page.SaveAsync();
        page.FocusEditor();
    }

    private static void OpenNewWindow()
    {
        if (Environment.ProcessPath is not string exe) return;
        Process.Start(new ProcessStartInfo(exe, LaunchOptions.NewWindowArgument) { UseShellExecute = false });
    }

    private void Session_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Models.Settings.SessionState.RecentFiles)) BuildRecentMenu();
    }

    private void BuildRecentMenu()
    {
        RecentMenu.Items.Clear();
        List<string> recent = ViewModel.Storage.State.Session.RecentFiles;

        if (recent.Count == 0 || LaunchOptions.IsNewWindow)
        {
            RecentMenu.Items.Add(new MenuFlyoutItem { Text = "No recent files", IsEnabled = false });
            return;
        }

        foreach (string path in recent)
        {
            var item = new MenuFlyoutItem { Text = Path.GetFileName(path) };
            ToolTipService.SetToolTip(item, path);
            item.Click += async (s, e) =>
            {
                if (Page is MainPage page) await page.OpenPathAsync(path);
            };
            RecentMenu.Items.Add(item);
        }

        RecentMenu.Items.Add(new MenuFlyoutSeparator());
        var clear = new MenuFlyoutItem { Text = "Clear recent files" };
        clear.Click += (s, e) =>
        {
            ViewModel.ClearRecentFiles();
            Page?.FocusEditor();
        };
        RecentMenu.Items.Add(clear);
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private async void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Page is not MainPage page) return;

        if (IsSettingsOpen)
        {
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                CloseSettings();
            }
            return;
        }

        bool ctrl = IsDown(VirtualKey.Control);
        bool shift = IsDown(VirtualKey.Shift);
        bool alt = IsDown(VirtualKey.Menu);

        if (e.Key == VirtualKey.Tab && !ctrl && !alt
            && FocusManager.GetFocusedElement(Content.XamlRoot) is RichEditBox
            && page.TryChangeListLevel(shift ? -1 : 1))
        {
            e.Handled = true;
            return;
        }
        VirtualKey key = e.Key;

        bool inTextBox = FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox;
        string? newTabType = NewTabTypeForKey(key);

        Func<Task>? action = (ctrl, shift, alt, key) switch
        {
            (true, false, false, VirtualKey.N) => () => { ShowNewMenu(); return Task.CompletedTask; },
            (true, false, false, _) when newTabType is not null => () => { OpenNewTab(newTabType); return Task.CompletedTask; },
            (true, false, false, VirtualKey.Z) when page.IsMarkdownMode && !inTextBox => () => { page.Undo(); return Task.CompletedTask; },
            (true, false, false, VirtualKey.Y) when page.IsMarkdownMode && !inTextBox => () => { page.Redo(); return Task.CompletedTask; },
            (true, true, false, VirtualKey.Z) when page.IsMarkdownMode && !inTextBox => () => { page.Redo(); return Task.CompletedTask; },
            (true, false, false, VirtualKey.B) when !inTextBox => () => { page.ToggleBold(); return Task.CompletedTask; },
            (true, false, false, VirtualKey.I) when !inTextBox => () => { page.ToggleItalic(); return Task.CompletedTask; },
            (true, false, false, VirtualKey.U) when !inTextBox => () => { page.ToggleUnderline(); return Task.CompletedTask; },
            (true, true, false, VirtualKey.N) => () => { OpenNewWindow(); return Task.CompletedTask; },
            (true, false, false, VirtualKey.O) => page.OpenFileAsync,
            (true, false, false, VirtualKey.S) => SaveActiveAsync,
            (true, true, false, VirtualKey.S) => page.SaveAsAsync,
            (true, false, true, VirtualKey.S) => page.SaveAllAsync,
            (true, false, false, VirtualKey.W) => page.CloseActiveTabAsync,
            (true, true, false, VirtualKey.W) => () => { RequestClose(); return Task.CompletedTask; },
            (true, false, false, VirtualKey.E) => page.DefineWithBingAsync,
            (true, false, false, VirtualKey.F) => () => { page.ShowFind(replace: false); return Task.CompletedTask; },
            (true, false, false, VirtualKey.H) => () => { page.ShowFind(replace: true); return Task.CompletedTask; },
            (true, false, false, VirtualKey.G) => page.GoToLineAsync,
            (false, false, false, VirtualKey.F3) => () => { page.FindNext(); return Task.CompletedTask; },
            (false, true, false, VirtualKey.F3) => () => { page.FindPrevious(); return Task.CompletedTask; },
            (true, _, false, VirtualKey.Add or (VirtualKey)187) => () => { page.ZoomIn(); return Task.CompletedTask; },
            (true, false, false, VirtualKey.Subtract or (VirtualKey)189) => () => { page.ZoomOut(); return Task.CompletedTask; },
            (true, false, false, VirtualKey.Number0 or VirtualKey.NumberPad0) => () => { page.ResetZoom(); return Task.CompletedTask; },
            (false, false, false, VirtualKey.Escape) when page.IsFindOpen => () => { page.CloseFind(); return Task.CompletedTask; },
            _ => null,
        };

        if (action is null) return;
        e.Handled = true;
        await action();
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (RootFrame.Content is MainPage mainPage)
        {
            await mainPage.SaveAsAsync();
        }
    }

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (RootFrame.Content is MainPage mainPage)
        {
            await mainPage.OpenFileAsync();
        }
    }
}
