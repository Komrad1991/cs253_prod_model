using System.Windows.Forms;

namespace ProdModel.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // тёмные системные элементы (полосы прокрутки, выпадающие списки) в Windows 11
        try { Application.SetColorMode(SystemColorMode.Dark); } catch { /* среда без поддержки */ }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += (_, e) => Log(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception);

        try
        {
            var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
            Application.Run(new MainForm(args));
        }
        catch (Exception ex)
        {
            Log(ex);
            throw;
        }
    }

    private static void Log(Exception? exception)
    {
        if (exception is null) return;
        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), exception.ToString());
        }
        catch
        {
            // журнал аварии — не критичен
        }
    }
}
