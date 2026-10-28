namespace DeepUninstaller;

/// <summary>
/// Revisa lo encontrado antes de mostrarlo y genera avisos para el usuario.
/// Lo más importante: detecta carpetas que contienen OTROS programas instalados
/// (p.ej. la carpeta de Steam con todos sus juegos) y las marca como peligrosas.
/// </summary>
public static class RiskAnalyzer
{
    // Ejecutables de launchers: si una carpeta los contiene, borrarla rompe el launcher y sus juegos
    private static readonly (string exe, string name)[] Launchers =
    {
        ("steam.exe", "Steam"), ("EpicGamesLauncher.exe", "Epic Games Launcher"), ("Battle.net.exe", "Battle.net"),
        ("Battle.net Launcher.exe", "Battle.net"), ("EADesktop.exe", "EA app"), ("upc.exe", "Ubisoft Connect"),
        ("RiotClientServices.exe", "Riot Client"), ("GalaxyClient.exe", "GOG Galaxy"),
    };

    public static List<string> Analyze(InstalledApp app, string installDir, List<LeftoverItem> items, IEnumerable<InstalledApp> allApps)
    {
        var others = allApps
            .Where(a => !ReferenceEquals(a, app) && a.Name != app.Name)
            .Select(a => (a.Name, Paths: AppPaths(a)))
            .Where(x => x.Paths.Count > 0)
            .ToList();

        foreach (var it in items.Where(i => i.Kind == LeftoverKind.Carpeta))
        {
            var hit = others
                .Where(o => o.Paths.Any(p => p.Equals(it.Path, StringComparison.OrdinalIgnoreCase) || SafePaths.IsUnder(p, it.Path)))
                .Select(o => o.Name)
                .ToList();
            foreach (var (exe, name) in Launchers)
            {
                bool isThisLauncher = app.Name.Contains(name, StringComparison.OrdinalIgnoreCase);
                if (!isThisLauncher && File.Exists(Path.Combine(it.Path, exe))) hit.Insert(0, name);
            }
            it.AffectedApps = hit.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        var warnings = new List<string>();

        if (app.InstallLocation.Length > 0 && !Directory.Exists(app.InstallLocation))
            warnings.Add($"La carpeta del programa NO está accesible: {app.InstallLocation}. Si está en un disco externo, " +
                         "conéctalo y vuelve a analizar. Si no, solo se quitará lo que ves en la lista (sus archivos no se tocarán).");

        var danger = items.Where(i => i.Danger).ToList();
        if (danger.Count > 0)
        {
            var names = danger.SelectMany(i => i.AffectedApps).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string list = string.Join(", ", names.Take(6)) + (names.Count > 6 ? $" y {names.Count - 6} más" : "");
            warnings.Add($"{danger.Count} carpeta(s) contienen OTROS programas: {list}. Vienen DESMARCADAS en rojo. " +
                         "Si las marcas, esos programas también se borrarán y dejarán de funcionar.");
        }

        if (app.Source == "Juego Steam")
            warnings.Add("Juego de Steam: cierra Steam antes de eliminar. Solo se borra este juego; Steam y tus otros juegos no se tocan.");
        if (app.Source == "Juego Epic")
            warnings.Add("Juego de Epic Games: cierra Epic Games Launcher antes de eliminar.");

        int possible = items.Count(i => !i.HighConfidence && !i.Danger);
        if (possible > 0)
            warnings.Add($"{possible} elemento(s) «Posible» solo tienen un nombre parecido. Vienen desmarcados: revísalos antes de marcarlos.");

        return warnings;
    }

    /// <summary>Rutas que identifican a un programa: su carpeta, su icono y su desinstalador.</summary>
    private static List<string> AppPaths(InstalledApp a)
    {
        var list = new List<string>();
        if (a.InstallLocation.Length > 2) list.Add(SafePaths.Norm(a.InstallLocation));
        string icon = a.DisplayIcon.Split(',')[0].Trim().Trim('"');
        if (icon.Length > 2 && Path.IsPathRooted(icon)) list.Add(SafePaths.Norm(icon));
        string un = LeftoverScanner.ExtractExe(a.UninstallString);
        if (un.Length > 0) list.Add(SafePaths.Norm(un));
        return list;
    }
}
