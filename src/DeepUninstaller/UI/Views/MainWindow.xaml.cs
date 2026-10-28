using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DeepUninstaller.UI.Views;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // Programas
    private List<InstalledApp> _apps = new();
    private ListCollectionView? _view;
    private int _loadVersion;
    private readonly List<CategoryFilter> _categories = new()
    {
        new() { Category = null, Label = "Todos", Title = "Todos los programas", Glyph = Icons.Apps },
        new() { Category = AppCategory.Juego, Label = "Juegos", Title = "Juegos y launchers", Glyph = Icons.Games },
        new() { Category = AppCategory.Programa, Label = "Programas", Title = "Programas", Glyph = Icons.Desktop },
        new() { Category = AppCategory.Desarrollo, Label = "Desarrollo", Title = "Herramientas de desarrollo", Glyph = Icons.Code },
        new() { Category = AppCategory.Componente, Label = "Componentes", Title = "Componentes del sistema", Glyph = Icons.Extension },
        new() { Category = AppCategory.Controlador, Label = "Controladores", Title = "Controladores", Glyph = Icons.Memory },
    };

    // Revisión
    private InstalledApp? _reviewApp;
    private readonly ObservableCollection<TreeNode> _roots = new();
    private readonly ListCollectionView _rootsView;
    private NodeGroup? _reviewFilter;
    private bool _summaryPending;

    // Limpieza
    private readonly List<CleanTarget> _targets = SystemCleaner.GetTargets();
    private bool _cleanAnalyzed;

    public MainWindow()
    {
        InitializeComponent();

        CategoryList.ItemsSource = _categories;
        CategoryList.SelectedIndex = 0;

        _rootsView = (ListCollectionView)CollectionViewSource.GetDefaultView(_roots);
        _rootsView.Filter = o => _reviewFilter == null || ((TreeNode)o).Group == _reviewFilter;
        ReviewTree.ItemsSource = _rootsView;
        TreeNode.AnyCheckChanged += ScheduleSummary;
        RunUninstallerCheck.Checked += (_, _) => UpdateSummary();
        RunUninstallerCheck.Unchecked += (_, _) => UpdateSummary();

        CleanList.ItemsSource = _targets;
        foreach (var t in _targets) t.PropertyChanged += (_, _) => UpdateCleanTotals();

        Loaded += async (_, _) => await LoadAppsAsync();
    }

    /// <summary>Barra de título oscura (Windows 10/11).</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            int on = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, sizeof(int));
        }
        catch { }
    }

    // ================================================================== GENERAL

    private void ShowBusy(string title, string text = "")
    {
        BusyTitle.Text = title;
        BusyText.Text = text;
        Overlay.Visibility = Visibility.Visible;
    }

    private void HideBusy() => Overlay.Visibility = Visibility.Collapsed;

    private void ShowView(Grid view)
    {
        ProgramsView.Visibility = view == ProgramsView ? Visibility.Visible : Visibility.Collapsed;
        ReviewView.Visibility = view == ReviewView ? Visibility.Visible : Visibility.Collapsed;
        CleanView.Visibility = view == CleanView ? Visibility.Visible : Visibility.Collapsed;

        bool inReview = view == ReviewView;
        NavUninstall.IsEnabled = NavClean.IsEnabled = CategoryList.IsEnabled = !inReview;
        var cats = view == CleanView ? Visibility.Collapsed : Visibility.Visible;
        CategoriesLabel.Visibility = CategoryList.Visibility = cats;
    }

    private async void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (sender == NavClean)
        {
            ShowView(CleanView);
            if (!_cleanAnalyzed) await AnalyzeCleanAsync();
        }
        else ShowView(ProgramsView);
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
            else if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch { }
    }

    private void OpenBackups_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Cleaner.BackupRoot);
        OpenInExplorer(Cleaner.BackupRoot);
    }

    // ================================================================== PROGRAMAS

    private async Task LoadAppsAsync()
    {
        int version = ++_loadVersion;
        ShowBusy("Leyendo programas instalados", "Buscando programas, juegos de Steam y juegos de Epic Games...");
        List<InstalledApp> apps;
        try { apps = await Task.Run(AppScanner.GetInstalledApps); }
        finally { HideBusy(); }

        _apps = apps;
        _view = new ListCollectionView(_apps) { Filter = FilterApp };
        ApplySort();
        AppsList.ItemsSource = _view;
        foreach (var c in _categories)
            c.Count = c.Category == null ? _apps.Count : _apps.Count(a => a.Category == c.Category);
        UpdateCount();
        ShowDetail(null);

        // Iconos reales en segundo plano para no bloquear la ventana
        _ = Task.Run(() =>
        {
            foreach (var a in apps)
            {
                if (version != _loadVersion) return;
                var icon = IconLoader.Load(a);
                if (icon != null) Dispatcher.BeginInvoke(() => a.Icon = icon);
            }
        });
    }

    private CategoryFilter? SelectedCategory => CategoryList.SelectedItem as CategoryFilter;

    private bool FilterApp(object o)
    {
        var a = (InstalledApp)o;
        var cat = SelectedCategory?.Category;
        if (cat != null && a.Category != cat) return false;
        string q = SearchBox.Text.Trim();
        return q.Length == 0 || a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplySort()
    {
        if (_view == null) return;
        using (_view.DeferRefresh())
        {
            _view.SortDescriptions.Clear();
            if (SortSize.IsChecked == true) _view.SortDescriptions.Add(new SortDescription(nameof(InstalledApp.SizeKB), ListSortDirection.Descending));
            else if (SortDate.IsChecked == true) _view.SortDescriptions.Add(new SortDescription(nameof(InstalledApp.InstallDate), ListSortDirection.Descending));
            _view.SortDescriptions.Add(new SortDescription(nameof(InstalledApp.Name), ListSortDirection.Ascending));
        }
    }

    private void UpdateCount()
    {
        if (_view == null) return;
        var cat = SelectedCategory ?? _categories[0];
        ProgramsTitle.Text = cat.Title;
        long totalKB = _view.Cast<InstalledApp>().Sum(a => a.SizeKB);
        CountText.Text = $"{_view.Count} {(_view.Count == 1 ? "elemento" : "elementos")}" + (totalKB > 0 ? $"  ·  {Util.FormatSize(totalKB * 1024)} en total" : "");
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _view?.Refresh();
        UpdateCount();
    }

    private void Sort_Checked(object sender, RoutedEventArgs e) => ApplySort();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view?.Refresh();
        UpdateCount();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAppsAsync();

    private void AppsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetail(AppsList.SelectedItem as InstalledApp);

    private void ShowDetail(InstalledApp? app)
    {
        EmptyDetail.Visibility = app == null ? Visibility.Visible : Visibility.Collapsed;
        DetailContent.Visibility = app == null ? Visibility.Collapsed : Visibility.Visible;
        DetailContent.DataContext = app;
        if (app != null)
            InstallPathText.Text = app.InstallLocation.Length > 0 ? app.InstallLocation : "El programa no la indica (se buscará por nombre).";
    }

    private void OpenInstallFolder_Click(object sender, RoutedEventArgs e)
    {
        if (AppsList.SelectedItem is InstalledApp a && a.InstallLocation.Length > 0) OpenInExplorer(a.InstallLocation);
    }

    private async void AppsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AppsList.SelectedItem is InstalledApp a) await AnalyzeAsync(a);
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (AppsList.SelectedItem is InstalledApp a) await AnalyzeAsync(a);
    }

    // ================================================================== REVISIÓN

    private async Task AnalyzeAsync(InstalledApp app)
    {
        var scanner = new LeftoverScanner(app);
        List<LeftoverItem> items;
        ShowBusy($"Analizando {app.Name}", "Buscando carpetas, caché, temporales, accesos directos, registro, servicios, tareas y reglas del firewall...");
        try { items = await Task.Run(() => { scanner.PreScan(); return scanner.Scan(); }); }
        finally { HideBusy(); }

        if (items.Count == 0 && !app.HasUninstaller)
        {
            Dialog.Show(this, "No hay nada que borrar", $"No se encontró ningún archivo ni registro de {app.Name}.", DialogKind.Info);
            return;
        }

        // Avisos: carpetas que contienen otros programas, carpeta no accesible, etc.
        var warnings = RiskAnalyzer.Analyze(app, scanner.InstallDir, items, _apps);
        WarningsList.ItemsSource = warnings;
        WarningsBox.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _reviewApp = app;
        _roots.Clear();
        foreach (var i in items.OrderByDescending(i => i.Danger)) _roots.Add(new TreeNode(i));

        ReviewTitle.Text = app.Name;
        ReviewIcon.Content = app;
        RunUninstallerCheck.IsChecked = app.HasUninstaller;
        RunUninstallerCheck.IsEnabled = app.HasUninstaller;
        RunUninstallerHint.Text = app.Source switch
        {
            "Juego Steam" => "Juego de Steam: se borra directamente. Cierra Steam antes de eliminar.",
            "Juego Epic" => "Juego de Epic Games: se borra directamente. Cierra Epic Games Launcher antes de eliminar.",
            _ => app.HasUninstaller
                ? "Recomendado: el programa se desinstala a sí mismo y después se borran los restos marcados."
                : "Este programa no tiene desinstalador oficial: se borrará directamente lo marcado."
        };

        FilterAll.Content = $"Todo ({_roots.Count})";
        FilterFiles.Content = $"Archivos y carpetas ({_roots.Count(r => r.Group == NodeGroup.Archivos)})";
        FilterRegistry.Content = $"Registro ({_roots.Count(r => r.Group == NodeGroup.Registro)})";
        FilterSystem.Content = $"Servicios y tareas ({_roots.Count(r => r.Group == NodeGroup.Sistema)})";
        _reviewFilter = null;
        FilterAll.IsChecked = true;
        _rootsView.Refresh();
        ReviewEmpty.Visibility = _roots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdateSummary();
        ShowView(ReviewView);
    }

    private void ReviewFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (_rootsView == null) return;
        _reviewFilter = sender == FilterFiles ? NodeGroup.Archivos
                      : sender == FilterRegistry ? NodeGroup.Registro
                      : sender == FilterSystem ? NodeGroup.Sistema
                      : null;
        _rootsView.Refresh();
        ReviewEmpty.Visibility = _rootsView.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private IEnumerable<TreeNode> VisibleRoots => _rootsView.Cast<TreeNode>().ToList();

    private void CheckAll_Click(object sender, RoutedEventArgs e) { foreach (var r in VisibleRoots) r.IsChecked = true; }
    private void CheckSafe_Click(object sender, RoutedEventArgs e) { foreach (var r in VisibleRoots) r.IsChecked = r.HighConfidence; }
    private void CheckNone_Click(object sender, RoutedEventArgs e) { foreach (var r in VisibleRoots) r.IsChecked = false; }

    /// <summary>Agrupa muchos cambios de casillas en una sola actualización del resumen.</summary>
    private void ScheduleSummary()
    {
        if (_summaryPending) return;
        _summaryPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _summaryPending = false;
            UpdateSummary();
        });
    }

    private List<LeftoverItem> CollectSelected()
    {
        var list = new List<LeftoverItem>();
        foreach (var r in _roots) r.CollectSelected(list);
        return list;
    }

    private void UpdateSummary()
    {
        if (_reviewApp == null) return;
        var sel = CollectSelected();
        SelectedSizeText.Text = Util.FormatSize(_roots.Sum(r => r.SelectedSize()));
        SelectedCountText.Text = $"{sel.Count} elementos marcados para borrar";
        DeleteBtn.IsEnabled = sel.Count > 0 || RunUninstallerCheck.IsChecked == true;
    }

    private void OpenNode_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TreeNode n) OpenInExplorer(n.Path);
    }

    private void CancelReview_Click(object sender, RoutedEventArgs e)
    {
        _reviewApp = null;
        _roots.Clear();
        ShowView(ProgramsView);
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_reviewApp is not { } app) return;
        var items = CollectSelected();
        bool runUninstaller = RunUninstallerCheck.IsChecked == true && app.HasUninstaller;
        if (items.Count == 0 && !runUninstaller) return;
        long size = _roots.Sum(r => r.SelectedSize());

        if (!ConfirmDeletion(app, items, size, runUninstaller)) return;

        if (runUninstaller)
        {
            ShowBusy($"Desinstalando {app.Name}", "Completa los pasos en la ventana del desinstalador. Cuando termine, se borrarán los restos marcados.");
            string note;
            try { note = await Cleaner.RunUninstallerAsync(app); }
            finally { HideBusy(); }

            if (note.Length > 0)
                Dialog.Show(this, "Aviso", note + "\n\nSe continuará con el borrado de lo marcado.", DialogKind.Warning, "Continuar");
            else
                Dialog.Show(this, "El desinstalador terminó",
                    "Si todavía tiene alguna ventana abierta, termínala antes de continuar.\n\nAl pulsar Continuar se borrarán los restos marcados.",
                    DialogKind.Info, "Continuar");
        }

        ShowBusy("Eliminando restos", "Preparando...");
        var progress = new Progress<string>(s => BusyText.Text = s);
        DeleteResult r;
        try { r = await Task.Run(() => Cleaner.DeleteItems(items, app.Name, progress)); }
        finally { HideBusy(); }

        string text = $"Elementos eliminados: {r.Deleted}\nEspacio liberado: {Util.FormatSize(r.FreedBytes)}";
        if (r.PendingReboot > 0) text += $"\nSe borrarán al reiniciar (estaban en uso): {r.PendingReboot}";
        if (r.Errors.Count > 0) text += $"\n\nNo se pudieron borrar ({r.Errors.Count}):\n" + string.Join("\n", r.Errors.Take(8));
        text += $"\n\nCopia del registro guardada en:\n{r.BackupDir}";
        Dialog.Show(this, r.Errors.Count == 0 ? $"{app.Name} eliminado" : "Eliminado con avisos", text,
            r.Errors.Count == 0 ? DialogKind.Success : DialogKind.Warning);

        _reviewApp = null;
        _roots.Clear();
        ShowView(ProgramsView);
        await LoadAppsAsync();
    }

    /// <summary>
    /// Resumen claro de lo que va a pasar. Si hay carpetas que contienen otros programas,
    /// pide una segunda confirmación nombrando esos programas.
    /// </summary>
    private bool ConfirmDeletion(InstalledApp app, List<LeftoverItem> items, long size, bool runUninstaller)
    {
        var sb = new System.Text.StringBuilder();
        if (runUninstaller) sb.AppendLine($"1. Se abrirá el desinstalador oficial de {app.Name}.\n2. Después se borrará lo siguiente:\n");
        else sb.AppendLine("Se borrará lo siguiente:\n");

        int Count(params LeftoverKind[] k) => items.Count(i => k.Contains(i.Kind));
        void Line(int n, string what) { if (n > 0) sb.AppendLine($"   •  {n} {what}"); }
        Line(Count(LeftoverKind.Carpeta), "carpeta(s)");
        Line(Count(LeftoverKind.Archivo), "archivo(s)");
        Line(Count(LeftoverKind.ClaveRegistro, LeftoverKind.ValorRegistro), "entrada(s) del registro");
        Line(Count(LeftoverKind.Servicio), "servicio(s)");
        Line(Count(LeftoverKind.Tarea), "tarea(s) programada(s)");
        sb.AppendLine($"\nEspacio total: {Util.FormatSize(size)}");

        var biggest = items.Where(i => i.SizeBytes > 0).OrderByDescending(i => i.SizeBytes).Take(5).ToList();
        if (biggest.Count > 0)
        {
            sb.AppendLine("\nLo más grande:");
            foreach (var b in biggest) sb.AppendLine($"   •  {b.Path}  ({Util.FormatSize(b.SizeBytes)})");
        }

        var danger = items.Where(i => i.Danger).ToList();
        var affected = danger.SelectMany(i => i.AffectedApps).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (danger.Count > 0)
            sb.AppendLine($"\n⚠ ATENCIÓN: marcaste {danger.Count} carpeta(s) que contienen OTROS programas. " +
                          $"También se borrarán: {string.Join(", ", affected.Take(10))}{(affected.Count > 10 ? "…" : "")}.");

        sb.AppendLine("\nLo que desmarcaste se conserva. Se guarda copia del registro. Los archivos borrados NO van a la papelera y no se pueden recuperar.");

        if (!Dialog.Show(this, $"¿Eliminar {app.Name}?", sb.ToString(), DialogKind.Danger, "Eliminar", "Cancelar")) return false;

        if (danger.Count > 0)
        {
            string msg = "Vas a borrar carpetas que NO son solo de " + app.Name + ".\n\nEstos programas dejarán de funcionar:\n\n"
                         + string.Join("\n", affected.Take(20).Select(a => "   •  " + a))
                         + (affected.Count > 20 ? $"\n   …y {affected.Count - 20} más" : "")
                         + "\n\nCarpetas:\n" + string.Join("\n", danger.Select(d => "   •  " + d.Path))
                         + "\n\nSi no estás 100% seguro, pulsa «No, revisar» y desmárcalas.";
            if (!Dialog.Show(this, "¿Seguro? Esto afecta a otros programas", msg, DialogKind.Danger,
                    "Sí, borrar también esos programas", "No, revisar"))
                return false;
        }
        return true;
    }

    // ================================================================== LIMPIEZA

    private async void AnalyzeClean_Click(object sender, RoutedEventArgs e) => await AnalyzeCleanAsync();

    private async Task AnalyzeCleanAsync()
    {
        _cleanAnalyzed = true;
        foreach (var t in _targets) t.SizeBytes = -1;
        CleanTotalBig.Text = "…";
        CleanTotalSub.Text = "Analizando...";
        CleanSelectedText.Text = "";
        foreach (var t in _targets)
            t.SizeBytes = await Task.Run(() => SystemCleaner.Measure(t));
        UpdateCleanTotals();
    }

    private void UpdateCleanTotals()
    {
        if (_targets.Any(t => t.SizeBytes < 0)) return;
        var sel = _targets.Where(t => t.IsChecked).ToList();
        CleanTotalBig.Text = Util.FormatSize(_targets.Sum(t => t.SizeBytes));
        CleanTotalSub.Text = "se pueden liberar en total";
        CleanSelectedText.Text = $"Seleccionado: {Util.FormatSize(sel.Sum(t => t.SizeBytes))} en {sel.Count} categorías";
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var sel = _targets.Where(t => t.IsChecked).ToList();
        if (sel.Count == 0) return;
        if (!Dialog.Show(this, "Limpiar el sistema",
                $"Se limpiarán {sel.Count} categorías ({Util.FormatSize(sel.Sum(t => Math.Max(0, t.SizeBytes)))}).\n\nLo que esté en uso se salta automáticamente.",
                DialogKind.Warning, "Limpiar", "Cancelar"))
            return;

        long freed = 0;
        try
        {
            foreach (var t in sel)
            {
                ShowBusy("Limpiando el sistema", t.Name);
                freed += await Task.Run(() => SystemCleaner.Clean(t));
            }
        }
        finally { HideBusy(); }

        Dialog.Show(this, "Limpieza terminada", $"Espacio liberado: {Util.FormatSize(freed)}", DialogKind.Success);
        await AnalyzeCleanAsync();
    }
}
