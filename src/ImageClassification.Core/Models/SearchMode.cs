namespace ImageClassification.Core.Models;

/// <summary>
/// Defines the search mode for comic cover/page search.
/// </summary>
public enum SearchMode
{
    /// <summary>
    /// Only the first page (cover) of each archive is indexed and searched.
    /// </summary>
    CoversOnly = 0,

    /// <summary>
    /// All image pages in each archive are indexed and searched individually.
    /// </summary>
    AllPages = 1
}