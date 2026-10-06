using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Xaml.Behaviors;

namespace ImageClassification.UI.Behaviors;

/// <summary>
/// Automatically hands keyboard focus to the list right after results are issued:
/// the VM replaces the <see cref="WatchedProperty"/> property (default "Results")
/// with a fresh non-empty collection, so once that collection is non-empty and the
/// list is visible (e.g. the "Results" tab is opened), focus moves to the list.
/// From that moment on the standard ListView keyboard scrolling works — PageDown /
/// arrows after a tile selection — without a mouse click on the grid first.
/// Attached from XAML via Interaction.Behaviors (no code-behind).
/// </summary>
public sealed class ListAutoFocusBehavior : Behavior<ListView>
{
    public static readonly DependencyProperty WatchedPropertyProperty =
        DependencyProperty.Register(nameof(WatchedProperty), typeof(string), typeof(ListAutoFocusBehavior),
            new PropertyMetadata("Results"));

    /// <summary>VM property (INotifyPropertyChanged) whose change triggers the auto-focus check.</summary>
    public string WatchedProperty
    {
        get => (string)GetValue(WatchedPropertyProperty);
        set => SetValue(WatchedPropertyProperty, value);
    }

    private INotifyPropertyChanged? _vm;

    // Set when the VM issues fresh results; consumed the first time the list becomes
    // visible (focus + selection reset to the first result). Tab switches without a new
    // search keep the user's current selection.
    private bool _freshResults;

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.DataContextChanged += OnDataContextChanged;
        AssociatedObject.IsVisibleChanged += OnIsVisibleChanged;
        Watch(AssociatedObject.DataContext);
        TryAutoFocus();
    }

    protected override void OnDetaching()
    {
        AssociatedObject.DataContextChanged -= OnDataContextChanged;
        AssociatedObject.IsVisibleChanged -= OnIsVisibleChanged;
        Unwatch();
        base.OnDetaching();
    }

    private void OnDataContextChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        Watch(e.NewValue);
        TryAutoFocus();
    }

    private void OnIsVisibleChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        // The user switches to the results tab after a search — grab focus now.
        if ((bool)e.NewValue) TryAutoFocus();
    }

    private void Watch(object? dataContext)
    {
        Unwatch();
        _vm = dataContext as INotifyPropertyChanged;
        if (_vm is not null)
            _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void Unwatch()
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = null;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != WatchedProperty) return;
        // The VM swaps the whole collection after each search — mark it as fresh results;
        // the dispatcher gets one cycle for the new items before the focus/selection update.
        _freshResults = true;
        AssociatedObject.Dispatcher.BeginInvoke(new Action(TryAutoFocus));
    }

    private void TryAutoFocus()
    {
        var list = AssociatedObject;
        if (!list.IsVisible) return;
        if (list.DataContext is not IList collection || collection.Count == 0) return;

        // Fresh search: the first result becomes the starting selection (so the arrow
        // keys work right away) and the view is brought to the top of the grid.
        if (_freshResults)
        {
            _freshResults = false;
            list.SelectedIndex = 0;
            list.ScrollIntoView(list.Items[0]); // virtualization-safe: brings the first item into view
        }

        if (!list.IsKeyboardFocusWithin)
            list.Focus();
    }
}