using System.Collections.ObjectModel;

namespace DeepUninstaller.UI.ViewModels;

/// <summary>
/// Nodo del árbol de revisión. Las raíces son los restos encontrados; las carpetas se pueden
/// expandir para ver (y desmarcar) su contenido. La casilla es de tres estados:
/// true = se borra entero, false = se conserva, null = se borra solo lo marcado dentro.
/// </summary>
public class TreeNode : Observable
{
    private const int MaxChildren = 300;

    /// <summary>Se dispara cuando cambia cualquier casilla (para refrescar el resumen).</summary>
    public static event Action? AnyCheckChanged;

    private bool? _isChecked;
    private bool _isExpanded;
    private bool _loaded;

    public TreeNode? Parent { get; }
    public LeftoverItem? Item { get; }
    public LeftoverKind Kind { get; }
    public string Path { get; }
    public string Title { get; }
    public string Detail { get; private set; } = "";
    public long SizeBytes { get; private set; }
    public bool HighConfidence { get; }
    public bool IsPlaceholder { get; private init; }
    /// <summary>Para el nodo "y N elementos más": rutas que agrupa.</summary>
    public List<string>? MorePaths { get; private init; }
    public ObservableCollection<TreeNode> Children { get; } = new();

    public bool IsRoot => Parent == null;
    public bool IsDir => Kind == LeftoverKind.Carpeta && MorePaths == null;
    public bool CanOpen => !IsPlaceholder && MorePaths == null && Kind is LeftoverKind.Carpeta or LeftoverKind.Archivo;
    public bool ShowCheck => !IsPlaceholder;
    public string ConfidenceText => !IsRoot ? "" : Danger ? "Peligro" : HighConfidence ? "Seguro" : "Posible";
    /// <summary>La carpeta contiene otros programas instalados.</summary>
    public bool Danger => Item?.Danger == true;
    public string AffectsText => Danger ? "También borraría: " + string.Join(", ", Item!.AffectedApps.Take(8)) + (Item.AffectedApps.Count > 8 ? "…" : "") : "";

    public NodeGroup Group => Kind switch
    {
        LeftoverKind.Carpeta or LeftoverKind.Archivo => NodeGroup.Archivos,
        LeftoverKind.ClaveRegistro or LeftoverKind.ValorRegistro => NodeGroup.Registro,
        _ => NodeGroup.Sistema
    };

    public string Glyph
    {
        get
        {
            if (IsPlaceholder) return Icons.Cached;
            if (MorePaths != null) return Icons.More;
            return Kind switch
            {
                LeftoverKind.Carpeta => Icons.Folder,
                LeftoverKind.Archivo => Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || Path.EndsWith(".url", StringComparison.OrdinalIgnoreCase)
                    ? Icons.Shortcut : Icons.File,
                LeftoverKind.ClaveRegistro => Icons.RegistryKey,
                LeftoverKind.ValorRegistro => Icons.RegistryValue,
                LeftoverKind.Servicio => Icons.Service,
                LeftoverKind.Tarea => Icons.Task,
                _ => Icons.File
            };
        }
    }

    public string KindText => Kind switch
    {
        LeftoverKind.Carpeta => "Carpeta",
        LeftoverKind.Archivo => "Archivo",
        LeftoverKind.ClaveRegistro => "Clave de registro",
        LeftoverKind.ValorRegistro => "Valor de registro",
        LeftoverKind.Servicio => "Servicio",
        LeftoverKind.Tarea => "Tarea programada",
        _ => ""
    };

    /// <summary>Raíz creada a partir de un resto encontrado.</summary>
    public TreeNode(LeftoverItem item)
    {
        Item = item;
        Kind = item.Kind;
        Path = item.Path;
        Title = item.DisplayPath;
        SizeBytes = item.SizeBytes;
        HighConfidence = item.HighConfidence;
        Detail = $"{KindText}  ·  {item.Reason}" + (SizeBytes > 0 ? $"  ·  {Util.FormatSize(SizeBytes)}" : "");
        _isChecked = item.HighConfidence && !item.Danger; // lo peligroso SIEMPRE empieza desmarcado
        if (IsDir) Children.Add(Placeholder(this));
    }

    private TreeNode(TreeNode parent, string path, bool isDir, long size)
    {
        Parent = parent;
        Kind = isDir ? LeftoverKind.Carpeta : LeftoverKind.Archivo;
        Path = path;
        Title = System.IO.Path.GetFileName(path);
        SizeBytes = size;
        HighConfidence = true;
        Detail = (isDir ? "Carpeta" : "Archivo") + (size > 0 ? $"  ·  {Util.FormatSize(size)}" : "  ·  vacío");
        _isChecked = parent._isChecked ?? false;
        if (isDir) Children.Add(Placeholder(this));
    }

    private TreeNode(TreeNode parent, string title)
    {
        Parent = parent;
        Path = "";
        Title = title;
    }

    private static TreeNode Placeholder(TreeNode parent) => new(parent, "Cargando contenido...") { IsPlaceholder = true };

    public bool? IsChecked
    {
        get => _isChecked;
        set => SetChecked(value ?? false, updateChildren: true, updateParent: true);
    }

    private void SetChecked(bool? value, bool updateChildren, bool updateParent)
    {
        if (_isChecked == value) return;
        _isChecked = value;
        OnChanged(nameof(IsChecked));
        if (updateChildren && value.HasValue)
            foreach (var c in Children)
                if (!c.IsPlaceholder) c.SetChecked(value, true, false);
        if (updateParent) Parent?.RecomputeFromChildren();
        AnyCheckChanged?.Invoke();
    }

    private void RecomputeFromChildren()
    {
        var real = Children.Where(c => !c.IsPlaceholder).ToList();
        if (real.Count == 0) return;
        bool? state = real.All(c => c.IsChecked == true) ? true
                    : real.All(c => c.IsChecked == false) ? false
                    : null;
        SetChecked(state, updateChildren: false, updateParent: true);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            _isExpanded = value;
            OnChanged();
            if (value && !_loaded && IsDir) _ = LoadChildrenAsync();
        }
    }

    private async Task LoadChildrenAsync()
    {
        _loaded = true;
        string dir = Path;
        var entries = await Task.Run(() =>
        {
            var list = new List<(string path, bool isDir, long size)>();
            try
            {
                foreach (var d in Directory.EnumerateDirectories(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    list.Add((d, true, Util.DirSize(d)));
                foreach (var f in Directory.EnumerateFiles(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    long len = 0;
                    try { len = new FileInfo(f).Length; } catch { }
                    list.Add((f, false, len));
                }
            }
            catch { }
            return list;
        });

        Children.Clear();
        foreach (var (p, isDir, size) in entries.Take(MaxChildren))
            Children.Add(new TreeNode(this, p, isDir, size));

        if (entries.Count > MaxChildren)
        {
            var rest = entries.Skip(MaxChildren).ToList();
            long restSize = rest.Sum(e => e.size);
            Children.Add(new TreeNode(this, $"y {rest.Count} elementos más")
            {
                MorePaths = rest.Select(e => e.path).ToList(),
            }.WithState(_isChecked ?? false, restSize));
        }
    }

    private TreeNode WithState(bool isChecked, long size)
    {
        _isChecked = isChecked;
        SizeBytes = size;
        Detail = $"Agrupados  ·  {Util.FormatSize(size)}";
        return this;
    }

    /// <summary>Tamaño de lo que está marcado para borrar.</summary>
    public long SelectedSize() => _isChecked switch
    {
        true => SizeBytes,
        null => Children.Sum(c => c.SelectedSize()),
        _ => 0
    };

    /// <summary>Convierte lo marcado en elementos concretos a borrar.</summary>
    public void CollectSelected(List<LeftoverItem> output)
    {
        if (IsPlaceholder) return;
        if (_isChecked == true)
        {
            if (MorePaths != null)
            {
                foreach (var p in MorePaths)
                    output.Add(new LeftoverItem { Kind = Directory.Exists(p) ? LeftoverKind.Carpeta : LeftoverKind.Archivo, Path = p, HighConfidence = true });
                return;
            }
            output.Add(Item ?? new LeftoverItem { Kind = Kind, Path = Path, HighConfidence = true, SizeBytes = SizeBytes });
        }
        else if (_isChecked == null)
        {
            foreach (var c in Children) c.CollectSelected(output);
        }
    }
}
