using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ImageClassification.UI.ViewModels;
using Microsoft.Xaml.Behaviors;

namespace ImageClassification.UI.Behaviors;

/// <summary>
/// Asks the view model to load the thumbnail of the tile this element belongs to the moment
/// the tile becomes visible. The virtualizing panel materializes only visible tiles, so the
/// requests that arrive are exactly the ones for the tiles the user is looking at right now;
/// combined with the LIFO thumbnail pipeline (newest request extracted/decoded first) that
/// means "the file under the cursor is read first" — no more sequential top-to-bottom filling
/// while the user has scrolled to the bottom. Attached from the item template (no code-behind).
/// Works for every results grid whose view model implements <see cref="IThumbnailRequester"/>
/// (comic covers, plain image search, ...) — the behavior itself is type-agnostic.
/// </summary>
public sealed class LoadThumbnailOnVisibleBehavior : Behavior<FrameworkElement>
{
    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.IsVisibleChanged += OnIsVisibleChanged;
        CheckVisibility(); // the tile may already be visible at attach time
    }

    protected override void OnDetaching()
    {
        AssociatedObject.IsVisibleChanged -= OnIsVisibleChanged;
        base.OnDetaching();
    }

    private void OnIsVisibleChanged(object? sender, DependencyPropertyChangedEventArgs e) => CheckVisibility();

    private void CheckVisibility()
    {
        if (!AssociatedObject.IsVisible) return;
        if (AssociatedObject.DataContext is null) return;
        FindViewModel(AssociatedObject)?.RequestThumbnail(AssociatedObject.DataContext);
    }

    /// <summary>
    /// Walks up the visual tree to the results ListView and returns its view model if it
    /// implements <see cref="IThumbnailRequester"/> (null when the tile is not inside a
    /// results grid, e.g. a preview popup that reuses the same item template).
    /// </summary>
    private static IThumbnailRequester? FindViewModel(DependencyObject element)
    {
        for (var cur = VisualTreeHelper.GetParent(element);
             cur is not null;
             cur = VisualTreeHelper.GetParent(cur))
        {
            if (cur is ListView listView)
            {
                return listView.DataContext as IThumbnailRequester;
            }
        }

        return null;
    }
}