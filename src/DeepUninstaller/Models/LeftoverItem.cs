namespace DeepUninstaller.Models;

/// <summary>Un resto encontrado por el escáner.</summary>
public class LeftoverItem
{
    public LeftoverKind Kind { get; set; }
    /// <summary>Ruta de archivo/carpeta, ruta de registro, nombre de servicio o de tarea.</summary>
    public string Path { get; set; } = "";
    /// <summary>Solo para ValorRegistro: nombre del valor dentro de la clave Path.</summary>
    public string ValueName { get; set; } = "";
    public bool HighConfidence { get; set; }
    public string Reason { get; set; } = "";
    public long SizeBytes { get; set; }
    /// <summary>Otros programas instalados que están DENTRO de esta carpeta (borrarla los rompería).</summary>
    public List<string> AffectedApps { get; set; } = new();
    public bool Danger => AffectedApps.Count > 0;

    public string DisplayPath => Kind == LeftoverKind.ValorRegistro ? $"{Path}  →  {ValueName}" : Path;
}
