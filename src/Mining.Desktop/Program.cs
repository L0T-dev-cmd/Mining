using System.Diagnostics;

namespace Mining.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var instance = new Mutex(true, @"Local\MineriaBitcoin.Desktop", out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("Minería Bitcoin ya está abierta. Usa su ventana para controlar la sesión.",
                "Minería Bitcoin", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        ApplicationConfiguration.Initialize();
        // The application only mines after the user presses Iniciar.
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (System.ComponentModel.Win32Exception) { /* Worker threads also use lower priority. */ }
        Application.Run(new MainForm());
    }
}
