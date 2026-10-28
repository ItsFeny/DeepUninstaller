namespace DeepUninstaller.Models;

/// <summary>Una categoría de limpieza del sistema (temporales, papelera, caché...).</summary>
public class CleanTarget : Observable
{
    private long _size = -1;
    private bool _isChecked = true;

    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>Carpeta y patrón. Con patrón "*" se vacía la carpeta entera (sin borrar la carpeta).</summary>
    public List<(string Dir, string Pattern)> Locations { get; init; } = new();
    public string Glyph { get; init; } = Icons.Delete;
    public bool IsRecycleBin { get; init; }
    public bool StopWindowsUpdate { get; init; }

    public long SizeBytes { get => _size; set { _size = value; OnChanged(); OnChanged(nameof(SizeText)); } }
    public bool IsChecked { get => _isChecked; set { _isChecked = value; OnChanged(); } }
    public string SizeText => _size < 0 ? "…" : Util.FormatSize(_size);
}
