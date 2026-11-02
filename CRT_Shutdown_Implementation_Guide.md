# Smooth CRT Shutdown Animation for a WPF Browser

## Goal

Replace the current CRT shutdown animation, which changes the actual WPF window's `Height`, `Width`, `Top`, and `Left`, with a smoother visual-only transition.

**Desired appearance:** The visible interface compresses vertically toward its center, becomes a bright glowing horizontal beam, shrinks horizontally to a dot, fades out, and then closes the window.

**Expected duration:** approximately 900–950 ms. **Technology:** WPF / C# / XAML. **No physical window resizing during the effect.**

## Why the existing approach stutters

The current implementation animates window geometry (`Height`, `Top`, `Width`, and `Left`), forcing Windows and WPF to resize or reposition the entire native window repeatedly. It also hides `RootGrid` before users see the interface collapsing, so the result can look like a black rectangle shrinking rather than the browser UI switching off.

Use a still image of the WPF interface (a snapshot) and animate its `ScaleTransform`, instead of changing the window's dimensions or position. The animation can then be handled primarily by WPF's rendering pipeline.

## Files / areas to modify

1. **MainWindow.xaml** (or the XAML containing `RootGrid` and `CrtShutdownOverlay`): replace the existing CRT overlay with the XAML below.
2. **MainWindow.xaml.cs** (or the corresponding code-behind): replace `PlayCrtShutdownAnimationAsync()` and update `AnimateElementPropertyAsync()`.
3. **Shutdown command / button handler:** call `await PlayCrtShutdownAnimationAsync()` once, then close the window after the animation. Do not close before the animation completes.

> **Important placement:** `CrtShutdownOverlay` must be a **sibling of `RootGrid` inside a parent container**, not a child of `RootGrid`. Otherwise setting `RootGrid.Visibility = Collapsed` will also hide the animation. It should be the last child of the shared parent so it draws on top. If your XAML structure differs, adapt the names and parentage without recreating the entire window layout.

## Phase 1: Replace the CRT overlay in XAML

```xml
<Grid x:Name="CrtShutdownOverlay"
      Background="Black"
      Visibility="Collapsed"
      IsHitTestVisible="False"
      ClipToBounds="True"
      Panel.ZIndex="999">

    <!-- Snapshot of the visible WPF user interface -->
    <Image x:Name="CrtFrame"
           Stretch="Fill"
           Opacity="1"
           RenderTransformOrigin="0.5,0.5">
        <Image.RenderTransform>
            <ScaleTransform x:Name="CrtFrameScale"
                            ScaleX="1"
                            ScaleY="1" />
        </Image.RenderTransform>
    </Image>

    <!-- White/cyan CRT phosphor beam -->
    <Grid x:Name="CrtBeam"
          Height="20"
          HorizontalAlignment="Stretch"
          VerticalAlignment="Center"
          RenderTransformOrigin="0.5,0.5"
          Opacity="0">
        <Grid.RenderTransform>
            <ScaleTransform x:Name="CrtBeamScale" ScaleX="1" />
        </Grid.RenderTransform>

        <!-- Outer soft glow -->
        <Border Height="8"
                Background="#9DEAFF"
                VerticalAlignment="Center"
                Opacity="0.7">
            <Border.Effect>
                <BlurEffect Radius="12" />
            </Border.Effect>
        </Border>

        <!-- Bright central line -->
        <Border Height="2"
                Background="#F4FFFF"
                VerticalAlignment="Center">
            <Border.Effect>
                <DropShadowEffect Color="#C9F8FF"
                                  BlurRadius="9"
                                  ShadowDepth="0"
                                  Opacity="0.9" />
            </Border.Effect>
        </Border>
    </Grid>
</Grid>
```

**Example parent structure (illustration only):**

```xml
<Grid x:Name="WindowContentHost" Background="Black">
    <Grid x:Name="RootGrid">
        <!-- Existing browser UI lives here -->
    </Grid>

    <!-- Paste CrtShutdownOverlay from above here -->
</Grid>
```

Do not duplicate `RootGrid` or `WindowContentHost` if they already exist. Preserve your existing UI.

## Phase 2: Add the necessary C# namespaces

```csharp
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
```

## Phase 3: Replace the shutdown method

Paste this in the window's code-behind. The existing `Motion.OutCubic` helper is not required in this replacement.

```csharp
private bool _crtShutdownInProgress;

private async Task PlayCrtShutdownAnimationAsync()
{
    if (_crtShutdownInProgress)
        return;

    _crtShutdownInProgress = true;

    try
    {
        if (RootGrid.ActualWidth <= 0 || RootGrid.ActualHeight <= 0)
            return;

        // Take a snapshot BEFORE hiding the current WPF content.
        var dpi = VisualTreeHelper.GetDpi(RootGrid);
        int width = Math.Max(1, (int)Math.Ceiling(
            RootGrid.ActualWidth * dpi.DpiScaleX));
        int height = Math.Max(1, (int)Math.Ceiling(
            RootGrid.ActualHeight * dpi.DpiScaleY));

        var snapshot = new RenderTargetBitmap(
            width,
            height,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);

        snapshot.Render(RootGrid);
        snapshot.Freeze();

        // Stop old animations to allow clean replay.
        CrtFrameScale.BeginAnimation(
            ScaleTransform.ScaleYProperty, null);
        CrtBeamScale.BeginAnimation(
            ScaleTransform.ScaleXProperty, null);
        CrtFrame.BeginAnimation(UIElement.OpacityProperty, null);
        CrtBeam.BeginAnimation(UIElement.OpacityProperty, null);

        // Reset all visual states.
        CrtFrame.Source = snapshot;
        CrtFrame.Opacity = 1;
        CrtFrameScale.ScaleX = 1;
        CrtFrameScale.ScaleY = 1;
        CrtBeam.Opacity = 0;
        CrtBeamScale.ScaleX = 1;

        WindowContentHost.Background = Brushes.Black;
        CrtShutdownOverlay.Visibility = Visibility.Visible;

        // Overlay contains a screenshot, so underlying WPF controls can be hidden.
        RootGrid.Visibility = Visibility.Collapsed;

        var collapseEase = new CubicEase
        {
            EasingMode = EasingMode.EaseIn
        };
        var smoothEase = new SineEase
        {
            EasingMode = EasingMode.EaseInOut
        };
        var fadeEase = new CubicEase
        {
            EasingMode = EasingMode.EaseOut
        };

        // PHASE A (480 ms): Squeeze the entire visible snapshot vertically.
        // Start glowing near the end to blend screen -> beam.
        await Task.WhenAll(
            AnimateElementPropertyAsync(
                CrtFrameScale,
                ScaleTransform.ScaleYProperty,
                1, 0.004,
                TimeSpan.FromMilliseconds(480), collapseEase),

            AnimateElementPropertyAsync(
                CrtBeam,
                UIElement.OpacityProperty,
                0, 1,
                TimeSpan.FromMilliseconds(190), fadeEase,
                TimeSpan.FromMilliseconds(290)),

            AnimateElementPropertyAsync(
                CrtFrame,
                UIElement.OpacityProperty,
                1, 0,
                TimeSpan.FromMilliseconds(130), fadeEase,
                TimeSpan.FromMilliseconds(350))
        );

        // PHASE B (~430 ms): Collapse line horizontally into a glowing point.
        await Task.WhenAll(
            AnimateElementPropertyAsync(
                CrtBeamScale,
                ScaleTransform.ScaleXProperty,
                1, 0.002,
                TimeSpan.FromMilliseconds(330), smoothEase),

            AnimateElementPropertyAsync(
                CrtBeam,
                UIElement.OpacityProperty,
                1, 0,
                TimeSpan.FromMilliseconds(130), fadeEase,
                TimeSpan.FromMilliseconds(300))
        );

        // Free the captured image and perform shutdown.
        CrtFrame.Source = null;
        Close();
    }
    finally
    {
        _crtShutdownInProgress = false;
    }
}
```

**Behavior note:** `Close()` really closes the WPF window. If this effect is for *hiding browser chrome* or another UI-only action, replace `Close()` with that action and restore the real content/overlay state appropriately. This example implements a full window exit.

## Phase 4: Replace the animation helper

This helper adds an optional start delay, enabling beam fade and snapshot collapse to overlap instead of appearing as disconnected steps.

```csharp
private Task AnimateElementPropertyAsync(
    IAnimatable target,
    DependencyProperty property,
    double from,
    double to,
    TimeSpan duration,
    IEasingFunction easing,
    TimeSpan? delay = null)
{
    var completion = new TaskCompletionSource<bool>(
        TaskCreationOptions.RunContinuationsAsynchronously);

    var animation = new DoubleAnimation
    {
        From = from,
        To = to,
        Duration = new Duration(duration),
        BeginTime = delay ?? TimeSpan.Zero,
        EasingFunction = easing,
        FillBehavior = FillBehavior.HoldEnd
    };

    animation.Completed += (_, _) =>
        completion.TrySetResult(true);

    target.BeginAnimation(property, animation);
    return completion.Task;
}
```

Only use this replacement if the same helper is not needed elsewhere with different assumptions. If other code calls the helper, the optional `delay` parameter preserves the original six-argument call style.

## Phase 5: Connect it to the exit action

For a dedicated Close or Exit button:

```csharp
private async void ExitButton_Click(object sender, RoutedEventArgs e)
{
    await PlayCrtShutdownAnimationAsync();
}
```

Do **not** call `Close()` before `await PlayCrtShutdownAnimationAsync()`. If you are intercepting `Window.Closing`, **cancel only the first close request**, let the animation run, then allow the final `Close()` to proceed; otherwise you can create a close-animation recursion loop. Programmatic closes, system shutdown, and unsaved-data prompts may need separate handling.

## Phase 6: Important browser-specific limitation (WebView2)

Aerio is a browser. WPF `RenderTargetBitmap.Render(RootGrid)` **does not reliably capture WebView2-rendered webpage pixels** because WebView2 may render outside the standard WPF visual tree. In that case, the browser controls may appear in the snapshot while the actual webpage is blank or black.

To produce a faithful full-browser CRT collapse:

1. Before showing the overlay, capture visible WebView2 content using the CoreWebView2 `CapturePreviewAsync` API (requires initialized WebView2).
2. Combine that webpage capture with your WPF frame/browser chrome at the correct location, DPI, and size, or place the webpage preview in a correctly positioned `Image` layered with the captured chrome.
3. Ensure the combined preview is fully ready before hiding the live browser controls.
4. Trigger the transform animation only after the snapshot is visible.
5. Handle capture failure by falling back to the WPF snapshot or a neutral fullscreen surface. Do not crash shutdown.

**Do not assume the sample above already solves WebView2 composition.** It demonstrates a smooth WPF shutdown; a complete Aerio implementation needs to account for WebView2 screenshots and your actual visual hierarchy.

## Phase 7: Acceptance checklist

- [ ] CRT animation overlays the **entire** intended browser area, not only a sidebar or inner panel.
- [ ] The normal UI stays visible until the snapshot appears; there is no intermediate black flash.
- [ ] The visual image compresses vertically from its center without moving the physical window.
- [ ] The beam glows softly and fades in near the end of the vertical collapse.
- [ ] The beam shrinks horizontally to a tiny center point and fades away.
- [ ] No obvious stutter when using maximized, normal, and high-DPI displays.
- [ ] Browser webpage content is visible in the snapshot or a documented fallback is used.
- [ ] Exit cannot be double-triggered by rapid clicking.
- [ ] Window closes only after the visual effect has finished.
- [ ] No lingering snapshot/image resources remain after exit.
- [ ] Build succeeds with no XAML name or C# compilation errors.

## Troubleshooting

| Symptom | Check / fix |
|---|---|
| Overlay is invisible | It must be a sibling of `RootGrid`, not inside it; set `Panel.ZIndex` and verify shared parent. |
| Flash of black before collapse | Snapshot must be ready and assigned to `CrtFrame.Source` before collapsing `RootGrid`. |
| Page is missing in snapshot | Use WebView2 `CapturePreviewAsync`, then compose the content with WPF chrome. |
| XAML cannot find `CrtFrameScale` | Check that XAML control names match code-behind and reside in the same namescope. |
| Window closes immediately | Remove other `Close()` calls or duplicate close handlers that bypass the async animation. |
| Effect is choppy | Do not animate actual window geometry. Avoid heavy visual effects on giant surfaces; reduce glow/blur if necessary. |
| Exit causes recursion | Guard any `Closing` handler so the final post-animation `Close()` is allowed to complete. |

## Final implementation requirement

The key change is to **animate the visual contents rather than the actual window**. Preserve existing window size, position, browser controls, and close behavior. Apply the effect only after capturing a complete, visible UI image, and shut down after the animation ends.

### Reference documentation

- Microsoft Learn — [Transforms Overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/transforms-overview)
- Microsoft Learn — [Animation Overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/animation-overview)
- Microsoft Learn — [CoreWebView2.CapturePreviewAsync](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.capturepreviewasync)
