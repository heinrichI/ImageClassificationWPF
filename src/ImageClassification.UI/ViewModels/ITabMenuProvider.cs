using System.Collections.Generic;

namespace ImageClassification.UI.ViewModels;

/// <summary>
/// Optional contract for tab ViewModels that contribute items to the context-aware main menu.
/// </summary>
public interface ITabMenuProvider
{
    /// <summary>Menu items shown when this tab is active.</summary>
    IReadOnlyList<TabMenuItem> MenuItems { get; }
}
