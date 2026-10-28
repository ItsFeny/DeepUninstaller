using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DeepUninstaller.UI.Views;

public enum DialogKind { Info, Success, Warning, Danger }

public partial class Dialog : Window
{
    private Dialog() => InitializeComponent();

    /// <summary>Muestra un diálogo con el estilo de la app. Devuelve true si se pulsó el botón principal.</summary>
    public static bool Show(Window owner, string title, string message, DialogKind kind = DialogKind.Info,
                            string primary = "Aceptar", string? secondary = null)
    {
        var d = new Dialog { Owner = owner };
        d.TitleText.Text = title;
        d.MessageText.Text = message;
        d.PrimaryBtn.Content = primary;
        d.SecondaryBtn.Content = secondary;
        d.SecondaryBtn.Visibility = secondary == null ? Visibility.Collapsed : Visibility.Visible;
        if (secondary == null) d.PrimaryBtn.IsCancel = true;

        var (glyph, fg, bg) = kind switch
        {
            DialogKind.Success => ("", "Ok", "OkSoft"),
            DialogKind.Warning => ("", "Warn", "WarnSoft"),
            DialogKind.Danger => ("", "Danger", "DangerSoft"),
            _ => ("", "Accent", "AccentSoft"),
        };
        d.IconGlyph.Text = glyph;
        d.IconGlyph.Foreground = (Brush)d.FindResource(fg);
        d.IconBg.Background = (Brush)d.FindResource(bg);
        if (kind == DialogKind.Danger)
        {
            d.PrimaryBtn.Style = (Style)d.FindResource("BtnDanger");
            // En acciones destructivas, Enter NO confirma: el botón por defecto es el de cancelar
            if (secondary != null) { d.PrimaryBtn.IsDefault = false; d.SecondaryBtn.IsDefault = true; }
        }

        return d.ShowDialog() == true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        try { DragMove(); } catch { }
    }

    private void Primary_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Secondary_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
