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
    public MainWindow()
    {
        Current = this;
        ViewModel = new MainViewModel();
        InitializeComponent();

        Title = ProjectPaths.AppName;
        AppTitleText.Text = ProjectPaths.AppName;

        if (!ThemeService.Apply(ViewModel.Settings.Appearance.Theme, RootGrid))
        {
            ThemeService.Apply(ThemeService.DefaultTheme, RootGrid);
        }
        BuildThemeMenu();
        BuildRecentMenu();
        BuildNewMenus();
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
        if (TabItems.ContainerFromItem(tab) is UIElement container)
        {
            container.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
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

    private void BuildThemeMenu()
    {
        foreach (ThemeInfo theme in ThemeService.Themes)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = theme.DisplayName,
                GroupName = "Theme",
                IsChecked = theme.Name == ThemeService.CurrentTheme,
            };
            item.Click += (s, e) => SelectTheme(theme.Name);
            ThemeMenu.Items.Add(item);
        }
    }

    private void SelectTheme(string name)
    {
        if (!ThemeService.Apply(name, RootGrid)) return;

        ViewModel.Settings.Appearance.Theme = name;
        (RootFrame.Content as MainPage)?.FocusEditor();
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

    private static readonly (string Text, string Extension, string Shortcut)[] NewTabTypes =
    {
        ("Plain text (.txt)", ".txt", "Ctrl+1"),
        ("Markdown (.md)", ".md", "Ctrl+2"),
        ("Rich text (.rtf)", ".rtf", "Ctrl+3"),
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
        foreach (Popup popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(Content.XamlRoot))
        {
            popup.IsOpen = false;
        }
        Page?.NewTab(extension);
    }

    private static string? NewTabTypeForKey(VirtualKey key) => key switch
    {
        VirtualKey.Number1 or VirtualKey.NumberPad1 => NewTabTypes[0].Extension,
        VirtualKey.Number2 or VirtualKey.NumberPad2 => NewTabTypes[1].Extension,
        VirtualKey.Number3 or VirtualKey.NumberPad3 => NewTabTypes[2].Extension,
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

        bool ctrl = IsDown(VirtualKey.Control);
        bool shift = IsDown(VirtualKey.Shift);
        bool alt = IsDown(VirtualKey.Menu);
        VirtualKey key = e.Key;

        bool inTextBox = FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox;
        string? newTabType = NewTabTypeForKey(key);

        Func<Task>? action = (ctrl, shift, alt, key) switch
        {
            (true, false, false, VirtualKey.N) => () => { ShowNewMenu(); return Task.CompletedTask; },
            (true, false, false, _) when newTabType is not null => () => { page.NewTab(newTabType); return Task.CompletedTask; },
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
