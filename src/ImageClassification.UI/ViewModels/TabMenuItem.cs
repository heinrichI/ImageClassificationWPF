using System.Windows.Input;

namespace ImageClassification.UI.ViewModels;

/// <summary>
/// A single menu item contributed by a tab ViewModel to the context-aware main menu.
/// </summary>
public sealed record TabMenuItem(string Header, ICommand Command, object? CommandParameter = null);
