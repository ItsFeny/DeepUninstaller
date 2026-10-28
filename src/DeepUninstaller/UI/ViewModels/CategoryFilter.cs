namespace DeepUninstaller.UI.ViewModels;

public class CategoryFilter : Observable
{
    private int _count;
    public AppCategory? Category { get; init; }
    public string Label { get; init; } = "";
    public string Title { get; init; } = "";
    public string Glyph { get; init; } = "";
    public int Count { get => _count; set { _count = value; OnChanged(); } }
}
