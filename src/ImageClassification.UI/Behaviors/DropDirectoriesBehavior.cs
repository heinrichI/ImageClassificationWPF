using System.Windows;
using System.Windows.Input;
using Microsoft.Xaml.Behaviors;

namespace ImageClassification.UI.Behaviors;

/// <summary>
/// Makes a panel a drop target for folders dragged in from Windows Explorer: on drop,
/// all valid directories of the drag are passed to the bound <see cref="Command"/> as
/// a <see cref="string[]"/>. Files and non-existent paths are ignored, so dropping a
/// mixed selection only adds the folders. The "copy" cursor is shown during DragOver
/// only when the drag contains at least one existing directory.
/// Attach from XAML to the target element (e.g. the "Select directories" GroupBox):
/// <code>
/// &lt;GroupBox.Behaviors&gt;
///     &lt;bh:DropDirectoriesBehavior Command="{Binding AddDirectoriesCommand}" /&gt;
/// &lt;/GroupBox.Behaviors&gt;
/// </code>
/// </summary>
public sealed class DropDirectoriesBehavior : Behavior<FrameworkElement>
{
    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(DropDirectoriesBehavior));

    /// <summary>Command that receives the dropped directories as a <see cref="string[]"/>.</summary>
    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.AllowDrop = true;
        AssociatedObject.DragOver += OnDragOver;
        AssociatedObject.Drop += OnDrop;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.DragOver -= OnDragOver;
        AssociatedObject.Drop -= OnDrop;
        base.OnDetaching();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDirectories(e.Data, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (Command is null || !TryGetDirectories(e.Data, out var directories))
            return;

        if (Command.CanExecute(directories))
            Command.Execute(directories);

        e.Handled = true;
    }

    /// <summary>
    /// Extracts the existing directories from a drag payload (Explorer drops arrive as
    /// <see cref="DataFormats.FileDrop"/> string arrays — folders included). Returns
    /// false when the payload carries no file list or not a single directory exists.
    /// </summary>
    internal static bool TryGetDirectories(object? data, out string[] directories)
    {
        if (data is DataObject dataObject
            && dataObject.GetDataPresent(DataFormats.FileDrop)
            && dataObject.GetData(DataFormats.FileDrop) is string[] paths)
        {
            directories = paths
                .Where(p => !string.IsNullOrWhiteSpace(p) && System.IO.Directory.Exists(p))
                .ToArray();

            return directories.Length > 0;
        }

        directories = [];
        return false;
    }
}