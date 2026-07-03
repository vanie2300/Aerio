using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace TabloidBrowser;

public class AnimatedTabPanel : Panel
{
    private readonly Dictionary<UIElement, TranslateTransform> _transforms = [];
    private int _dragIndex = -1;
    private int _insertIndex = -1;
    private Point _lastIndexChangePos;
    private double _tabWidth = 210;

    public void BeginDrag(int tabIndex)
    {
        _dragIndex = tabIndex;
        _insertIndex = tabIndex;
        _lastIndexChangePos = new Point(double.MinValue, 0);
        foreach (var kv in _transforms)
            kv.Value.X = 0;
        if (GetChild(tabIndex) is { } child)
            SetZIndex(child, 1000);
    }

    public void UpdateInsertion(int dragIndex, int insertIndex, double dragOffsetX, Point panelPos)
    {
        _dragIndex = dragIndex;
        if (_insertIndex != insertIndex)
        {
            if (Math.Abs(panelPos.X - _lastIndexChangePos.X) > 5)
            {
                _lastIndexChangePos = panelPos;
                _insertIndex = insertIndex;
                RecomputeTargets();
            }
        }
        var child = GetChild(dragIndex);
        if (child is not null && _transforms.TryGetValue(child, out var dt))
            dt.X = dragOffsetX;
    }

    public void EndDrag()
    {
        if (GetChild(_dragIndex) is { } child)
            SetZIndex(child, 0);
        foreach (var kv in _transforms)
            kv.Value.X = 0;
        _dragIndex = -1;
        _insertIndex = -1;
    }

    public void AbortDrag()
    {
        if (GetChild(_dragIndex) is { } child)
            SetZIndex(child, 0);
        foreach (var kv in _transforms)
            kv.Value.X = 0;
        _dragIndex = -1;
        _insertIndex = -1;
    }

    public int GetInsertIndex(Point panelPos)
    {
        int visualIdx = 0;
        double accumulatedX = 0;

        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (i == _dragIndex)
            {
                continue;
            }

            var m = (child as FrameworkElement)?.Margin ?? new Thickness();
            double leftEdge = accumulatedX + m.Left +
                (_transforms.TryGetValue(child, out var t) ? t.X : 0);
            double midpoint = leftEdge + _tabWidth / 2;

            if (panelPos.X < midpoint)
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

    private void RecomputeTargets()
    {
        if (_dragIndex < 0 || _insertIndex < 0) return;
        double step = GetTabStep();

        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (i == _dragIndex)
                continue;

            double target = 0;
            if (_dragIndex < _insertIndex)
            {
                if (i > _dragIndex && i <= _insertIndex)
                    target = -step;
            }
            else
            {
                if (i >= _insertIndex && i < _dragIndex)
                    target = step;
            }

            GetOrCreateTransform(child).X = target;
        }
    }

    private double GetTabStep()
    {
        return _tabWidth > 0 ? _tabWidth : 210;
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
        double totalWidth = _tabWidth * tabCount;
        foreach (UIElement child in Children)
        {
            child.Measure(constraint);
        }

        return new Size(totalWidth + totalMargin, availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        double availableHeight = finalSize.Height;

        foreach (UIElement child in Children)
        {
            var m = (child as FrameworkElement)?.Margin ?? new Thickness();
            double arrangeX = x + m.Left;
            double childHeight = availableHeight - m.Top - m.Bottom;

            GetOrCreateTransform(child);

            child.Arrange(new Rect(arrangeX, m.Top, Math.Max(0, _tabWidth), Math.Max(0, childHeight)));
            x += _tabWidth;
        }

        return new Size(x, finalSize.Height);
    }

    private TranslateTransform GetOrCreateTransform(UIElement child)
    {
        if (_transforms.TryGetValue(child, out var t))
            return t;

        t = child.RenderTransform as TranslateTransform;
        if (t is null)
        {
            t = new TranslateTransform();
            child.RenderTransform = t;
        }
        _transforms[child] = t;
        return t;
    }
}
