# Cómo contribuir

¡Gracias por tu interés en mejorar Deep Uninstaller!

## Reportar errores o pedir funciones

Abre un [issue](../../issues) usando la plantilla correspondiente. Incluye tu versión de Windows, la versión de la app y los pasos para reproducir el problema.

## Enviar cambios

1. Haz un fork del repositorio y crea una rama: `git checkout -b mi-cambio`.
2. Compila y prueba localmente: `dotnet build -c Release`.
3. Sigue el estilo del código existente (C# con nullable habilitado, comentarios en español).
4. Haz commits pequeños y con mensajes claros.
5. Abre un Pull Request describiendo qué cambia y por qué.

## Seguridad ante todo

Esta aplicación borra archivos y claves del registro con permisos de administrador. Cualquier cambio en `Cleaner.cs`, `LeftoverScanner*.cs`, `SafePaths.cs` o `RiskAnalyzer.cs` debe:

- Nunca borrar rutas del sistema protegidas.
- Preferir falsos negativos (dejar un resto) antes que falsos positivos (borrar algo ajeno).
- Probarse en una máquina virtual o entorno desechable.
