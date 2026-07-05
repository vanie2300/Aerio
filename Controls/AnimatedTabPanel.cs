using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
namespace TabloidBrowser;

public class AnimatedTabPanel : Panel
{
    private readonly HashSet<UIElement> _animatedIn = [];
    private TranslateTransform? _dragTransform;
    private int _dragIndex = -1;
    private int _insertIndex = -1;
    private double _tabWidth = 210;
    private double _grabOffset;

    public double CurrentTabWidth => _tabWidth;

    protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
    {
        if (visualRemoved is UIElement removed)
            _animatedIn.Remove(removed);
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
    }

    public void BeginDrag(int tabIndex, Point panelPos)
    {
        _dragIndex = tabIndex;
        _insertIndex = tabIndex;
        _dragTransform = new TranslateTransform();
        if (GetChild(tabIndex) is { } child)
        {
            child.RenderTransform = _dragTransform;
            SetZIndex(child, 1000);
            var m = (child as FrameworkElement)?.Margin ?? new Thickness();
            _grabOffset = panelPos.X - (tabIndex * _tabWidth + m.Left);
        }
        InvalidateArrange();
    }

    public void UpdateInsertion(int dragIndex, int insertIndex, Point panelPos)
    {
        _dragIndex = dragIndex;
        if (_insertIndex != insertIndex)
        {
            _insertIndex = insertIndex;
            InvalidateArrange();
        }
        if (_dragTransform is not null)
        {
            var m = (GetChild(dragIndex) as FrameworkElement)?.Margin ?? new Thickness();
            double arrangedX = _insertIndex * _tabWidth + m.Left;
            _dragTransform.BeginAnimation(TranslateTransform.XProperty, null);
            _dragTransform.X = panelPos.X - arrangedX - _grabOffset;
        }
    }

    public void SetDetaching(bool isDetaching)
    {
        if (GetChild(_dragIndex) is { } child)
        {
            child.Visibility = isDetaching ? Visibility.Hidden : Visibility.Visible;
        }
    }

    public void EndDrag()
    {
        if (GetChild(_dragIndex) is { } child)
        {
            child.Visibility = Visibility.Visible;
            SetZIndex(child, 0);
            child.RenderTransform = null;
        }
        _dragTransform = null;
        _dragIndex = -1;
        _insertIndex = -1;
        InvalidateArrange();
    }

    public void AbortDrag()
    {
        if (GetChild(_dragIndex) is { } child)
        {
            child.Visibility = Visibility.Visible;
            SetZIndex(child, 0);
            child.RenderTransform = null;
        }
        _dragTransform = null;
        _dragIndex = -1;
        _insertIndex = -1;
        InvalidateArrange();
    }

    public int GetInsertIndex(Point panelPos)
    {
        int visualIdx = 0;
        double accumulatedX = 0;

        for (int i = 0; i < Children.Count; i++)
        {
            if (i == _dragIndex)
                continue;

            var m = (Children[i] as FrameworkElement)?.Margin ?? new Thickness();
            double leftEdge = accumulatedX + m.Left;
            double rightEdge = leftEdge + _tabWidth;

            if (panelPos.X < rightEdge)
                return visualIdx;

            visualIdx++;
            accumulatedX += _tabWidth;
        }
        return visualIdx;
    }

    private UIElement? GetChild(int index)
    {
        if (index >= 0 && index < Children.Count)
            return Children[index];
        return null;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        const double minTabWidth = 48;
        const double maxTabWidth = 210;

        int tabCount = Children.Count;
        if (tabCount == 0)
            return new Size(0, availableSize.Height);

        double totalMargin = 0;
        foreach (UIElement child in Children)
        {
            var m = (child as FrameworkElement)?.Margin ?? new Thickness();
            totalMargin += m.Left + m.Right;
        }

        double availableForTabs = Math.Max(0, availableSize.Width - totalMargin);
        _tabWidth = Math.Clamp(availableForTabs / tabCount, minTabWidth, maxTabWidth);

        var constraint = new Size(_tabWidth, availableSize.Height);
        foreach (UIElement child in Children)
        {
            child.Measure(constraint);
        }

        return new Size(_tabWidth * tabCount + totalMargin, availableSize.Height);
    }

    private void AnimateSlide(UIElement child, double fromX, double toX)
    {
        var translate = new TranslateTransform(fromX - toX, 0);
        child.RenderTransform = translate;
        Dispatcher.BeginInvoke(() =>
        {
            if (!Children.Contains(child)) return;
            var slide = new DoubleAnimation(fromX - toX, 0, TimeSpan.FromSeconds(0.12))
            {
                EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut }
            };
            translate.BeginAnimation(TranslateTransform.XProperty, slide);
        }, DispatcherPriority.Loaded);
    }

    private void AnimateEntry(UIElement child)
    {
        if (_animatedIn.Contains(child)) return;
        _animatedIn.Add(child);
        child.Opacity = 0;
        child.RenderTransform = new ScaleTransform(0.85, 0.85);
        child.RenderTransformOrigin = new Point(0.5, 0.5);
        Dispatcher.BeginInvoke(() =>
        {
            if (!Children.Contains(child)) return;
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.2))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var scaleUp = new DoubleAnimation(0.85, 1, TimeSpan.FromSeconds(0.25))
            {
                EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut }
            };
            child.BeginAnimation(OpacityProperty, fadeIn);
            child.RenderTransformOrigin = new Point(0.5, 0.5);
            var transform = child.RenderTransform as ScaleTransform;
            if (transform is not null)
            {
                transform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleUp);
                transform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleUp);
            }
        }, DispatcherPriority.Loaded);
    }

    private readonly Dictionary<UIElement, double> _lastX = [];

    protected override Size ArrangeOverride(Size finalSize)
    {
        double availableHeight = finalSize.Height;

        if (_dragIndex < 0)
        {
            double x = 0;
            foreach (UIElement child in Children)
            {
                var m = (child as FrameworkElement)?.Margin ?? new Thickness();
                child.Arrange(new Rect(x + m.Left, m.Top, Math.Max(0, _tabWidth), Math.Max(0, availableHeight - m.Top - m.Bottom)));
                AnimateEntry(child);
                _lastX[child] = x + m.Left;
                x += _tabWidth;
            }
            return new Size(x, finalSize.Height);
        }

        for (int i = 0; i < Children.Count; i++)
        {
            if (i == _dragIndex) continue;

            var child = Children[i];
            var m = (child as FrameworkElement)?.Margin ?? new Thickness();

            int visualIdx = i > _dragIndex ? i - 1 : i;
            double arrangeX = (visualIdx >= _insertIndex ? visualIdx + 1 : visualIdx) * _tabWidth + m.Left;

            if (_lastX.TryGetValue(child, out var prevX) && Math.Abs(prevX - arrangeX) > 0.5 && !_animatedIn.Contains(child))
                AnimateSlide(child, prevX, arrangeX);

            child.Arrange(new Rect(arrangeX, m.Top, Math.Max(0, _tabWidth), Math.Max(0, availableHeight - m.Top - m.Bottom)));
            _lastX[child] = arrangeX;
        }

        if (GetChild(_dragIndex) is { } draggedChild)
        {
            var m = (draggedChild as FrameworkElement)?.Margin ?? new Thickness();
            double arrangeX = _insertIndex * _tabWidth + m.Left;
            draggedChild.Arrange(new Rect(arrangeX, m.Top, Math.Max(0, _tabWidth), Math.Max(0, availableHeight - m.Top - m.Bottom)));
        }

        return new Size(_tabWidth * Children.Count, finalSize.Height);
    }
}
