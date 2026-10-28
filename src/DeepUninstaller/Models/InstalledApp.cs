using System.Windows.Media;

namespace DeepUninstaller.Models;

public class InstalledApp : Observable
{
    private ImageSource? _icon;

    public string Name { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Version { get; set; } = "";
    public string InstallLocation { get; set; } = "";
    public string UninstallString { get; set; } = "";
    public string QuietUninstallString { get; set; } = "";
    public string DisplayIcon { get; set; } = "";
    /// <summary>Ruta tipo "HKLM\SOFTWARE\...\Uninstall\X" (vista 64 bits). Vacío si no viene del registro.</summary>
    public string RegistryKey { get; set; } = "";
    public long SizeKB { get; set; }
    public DateTime? InstallDate { get; set; }
    public string Source { get; set; } = "Programa";
    /// <summary>Manifiesto del launcher (Epic) que también hay que borrar.</summary>
    public string ManifestFile { get; set; } = "";
    public AppCategory Category { get; set; }

    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; OnChanged(); OnChanged(nameof(HasIcon)); }
    }
    public bool HasIcon => _icon != null;

    public string CategoryLabel => Categorizer.Label(Category);
    public string CategoryGlyph => Categorizer.Glyph(Category);
    public string SizeText => SizeKB <= 0 ? "" : Util.FormatSize(SizeKB * 1024);
    public string DateText => InstallDate?.ToString("dd/MM/yyyy") ?? "—";
    public string VersionText => string.IsNullOrWhiteSpace(Version) ? "—" : Version;
    public string SizeDetailText => SizeKB <= 0 ? "Desconocido" : SizeText;
    public bool HasUninstaller => UninstallString.Length > 0 && !UninstallString.StartsWith("steam://", StringComparison.OrdinalIgnoreCase);
    public string UninstallerText => HasUninstaller ? "Sí, tiene desinstalador oficial" : "No (se borrará directamente)";
    public string PublisherText => string.IsNullOrWhiteSpace(Publisher) ? "Fabricante desconocido" : Publisher;
    public string Subtitle
    {
        get
        {
            var parts = new List<string> { PublisherText };
            if (!string.IsNullOrWhiteSpace(Version)) parts.Add(Version);
            return string.Join("  ·  ", parts);
        }
    }
}
