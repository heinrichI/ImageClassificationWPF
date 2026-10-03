using System.Windows.Input;

namespace ImageClassification.UI.ViewModels;

/// <summary>
/// A menu item contributed by a tab ViewModel to the context-aware main menu.
/// <see cref="Children">, when non-null, renders a submenu (the item is a group header);
/// such items may have a null <see cref="Command"/>.
/// </summary>
public sealed record TabMenuItem(string Header, ICommand? Command = null, object? CommandParameter = null,
    IReadOnlyList<TabMenuItem>? Children = null);
