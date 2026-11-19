using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DownloadManager.Presentation.ViewModels;

namespace DownloadManager.App;

/// <summary>
/// Always-on-top floating on-screen download tracker pill / mini icon badge.
/// Automatically appears in the bottom-right corner of the screen whenever downloads are active.
/// </summary>
public partial class FloatingTrackerWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _hideDelayTimer;
    private bool _hasPositioned;
    private bool _isMiniMode;

    public FloatingTrackerWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        _hideDelayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _hideDelayTimer.Tick += (_, _) =>
        {
            _hideDelayTimer.Stop();
            if (!_vm.HasFloatingTracker) Hide();
        };

        Loaded += (_, _) => EnsureBottomRightPosition();
        SizeChanged += (_, _) =>
        {
            if (!_hasPositioned) EnsureBottomRightPosition();
        };
    }

    public void SyncVisibility(bool hasActive)
    {
        if (hasActive)
        {
            _hideDelayTimer.Stop();
            if (!IsVisible)
            {
                Show();
                EnsureBottomRightPosition();
            }
        }
        else if (IsVisible)
        {
            // Keep visible briefly at the end so user sees completion before gently fading away
            _hideDelayTimer.Stop();
            _hideDelayTimer.Start();
        }
    }

    private void EnsureBottomRightPosition()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 16;
        Top = area.Bottom - ActualHeight - 16;
        _hasPositioned = true;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        var startPos = new Point(Left, Top);
        try { DragMove(); } catch { }
        if (_isMiniMode && Math.Abs(Left - startPos.X) < 3 && Math.Abs(Top - startPos.Y) < 3)
        {
            SetMiniMode(false);
        }
    }

    private void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_isMiniMode)
        {
            SetMiniMode(false);
        }
        else if (_vm.OpenActiveProgressCommand.CanExecute(null))
        {
            _vm.OpenActiveProgressCommand.Execute(null);
        }
    }

    private void OnCollapseToMini(object sender, RoutedEventArgs e)
    {
        SetMiniMode(true);
    }

    private void SetMiniMode(bool mini)
    {
        var rightEdge = Left + ActualWidth;
        var bottomEdge = Top + ActualHeight;

        _isMiniMode = mini;
        ExpandedCard.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        MiniBadge.Visibility = mini ? Visibility.Visible : Visibility.Collapsed;
        UpdateLayout();

        if (_hasPositioned && ActualWidth > 0 && ActualHeight > 0)
        {
            Left = Math.Max(8, rightEdge - ActualWidth);
            Top = Math.Max(8, bottomEdge - ActualHeight);
        }
    }
}
