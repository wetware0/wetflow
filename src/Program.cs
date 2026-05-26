using NAudio.MediaFoundation;

namespace WetFlow;

static class Program
{
    private const string MutexName = "WetFlow_SingleInstance_Mutex";

    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew) return; // Already running

        MediaFoundationApi.Startup();
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp());
        }
        finally
        {
            MediaFoundationApi.Shutdown();
        }
    }
}
