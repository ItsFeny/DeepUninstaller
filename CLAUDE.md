# CLAUDE.md

Guía para trabajar en este repositorio (para Claude Code y para cualquier persona que contribuya).

## Qué es

Deep Uninstaller: aplicación WPF (.NET 8, solo Windows) que ejecuta el desinstalador oficial de un programa, busca los restos que deja (carpetas, archivos, claves y valores del registro, servicios, tareas programadas) y los borra tras la confirmación del usuario. También limpia temporales y cachés del sistema. Se ejecuta como administrador (`app.manifest` → `requireAdministrator`). Toda la interfaz y los comentarios están en español.

## Comandos

```powershell
dotnet build DeepUninstaller.sln -c Release
dotnet publish src/DeepUninstaller/DeepUninstaller.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

No hay tests automatizados. La app pide elevación (UAC), así que no se puede ejecutar desde una sesión no interactiva: verifica con `dotnet build` (debe quedar en 0 advertencias) y pide al usuario que pruebe la interfaz.

## Arquitectura

```
src/DeepUninstaller/
  Models/              Datos puros + Observable (INotifyPropertyChanged)
  Services/Scanning/   Lectura: qué hay instalado y qué restos deja
  Services/Cleaning/   Escritura: desinstalar, respaldar y borrar
  Infrastructure/      Utilidades de bajo nivel (registro, rutas protegidas, tamaños)
  UI/Views/            Ventanas XAML + code-behind
  UI/ViewModels/       Objetos enlazados a la UI (TreeNode, CategoryFilter)
  UI/Themes/Theme.xaml Colores, estilos y la fuente de iconos
```

Flujo de desinstalación:
1. `AppScanner.GetInstalledApps()` lee las claves `Uninstall` de HKLM/HKCU (más Steam y Epic) y crea `InstalledApp`. `Categorizer` asigna una `AppCategory`.
2. `Cleaner.RunUninstallerAsync()` lanza el desinstalador oficial y espera también a las copias que NSIS/Inno dejan en `%TEMP%`.
3. `LeftoverScanner` (partial: archivos en `LeftoverScanner.cs`, registro en `LeftoverScanner.Registry.cs`) devuelve `LeftoverItem` con `HighConfidence` y `Reason`.
4. `RiskAnalyzer.Analyze()` marca como peligrosos los restos que contienen otros programas o launchers (`AffectedApps`) y genera avisos.
5. La UI muestra un árbol de `TreeNode` (casilla de tres estados). Lo peligroso siempre empieza desmarcado.
6. `Cleaner` respalda el registro en `Documentos\DeepUninstaller\Respaldos` y borra; lo bloqueado se programa con `MoveFileEx(DELAY_UNTIL_REBOOT)`.

## Reglas de seguridad (críticas)

Esta app borra con permisos de administrador. Cualquier cambio en `Services/` o en `Infrastructure/SafePaths.cs` debe cumplir:
- Toda ruta a borrar pasa por `SafePaths`. Nunca debilites esas comprobaciones.
- Ante la duda, `HighConfidence = false`: mejor dejar un resto que borrar algo ajeno.
- No borres nada sin pasar por la pantalla de revisión, y respalda el registro siempre antes de tocarlo.
- Accede al registro con `RegUtil` (siempre en vista de 64 bits) y con rutas de texto `HKLM\...` / `HKCU\...`.

## Convenciones

- Un tipo por archivo. El namespace coincide con la carpeta (`DeepUninstaller.Services.Scanning`, etc.).
- Los namespaces del proyecto son *global usings* en el `.csproj`: no añadas `using DeepUninstaller.*` en los archivos. Si creas una carpeta o namespace nuevo, regístralo allí.
- Usa namespaces de un solo archivo (`namespace X;`), nullable habilitado, campos privados de instancia en `_camelCase` y `static readonly` / `const` en PascalCase.
- Comentarios y textos de la interfaz en español, con `/// <summary>` en los tipos y métodos públicos que no sean obvios.
- Los iconos son glifos de Material Icons: añádelos como constantes en `UI/Icons.cs` y nunca escribas el carácter directamente.
- Los colores y estilos van en `UI/Themes/Theme.xaml`, no en línea en las vistas. Las imágenes se referencian con rutas absolutas (`/Assets/...`).
- Los errores de E/S y del registro se ignoran a propósito (`catch { }`) para que un elemento corrupto no detenga el escaneo. Mantén ese patrón en los bucles de escaneo, pero nunca ocultes errores en el flujo de borrado: van a `DeleteResult.Errors`.

## Publicar una versión

1. Sube `<Version>` en `src/DeepUninstaller/DeepUninstaller.csproj`.
2. Haz el commit y crea la etiqueta: `git tag vX.Y.Z && git push origin main --tags`.
3. `.github/workflows/build.yml` compila y crea el Release con dos ejecutables: el portable (autocontenido) y el ligero (requiere .NET 8).
