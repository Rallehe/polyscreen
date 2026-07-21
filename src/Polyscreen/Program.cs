using System.Runtime.InteropServices;

namespace Polyscreen;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
    private const int AttachParentProcess = -1;

    [STAThread]
    private static int Main(string[] args)
    {
        // With arguments we act as a CLI client talking to the running instance.
        if (args.Length > 0)
        {
            AttachConsole(AttachParentProcess);
            var response = PipeServer.SendCommand(args, out var error);
            Console.WriteLine();
            Console.WriteLine(response ?? $"Polyscreen did not answer: {error}");
            return response == null ? 1 : 0;
        }

        using var mutex = new Mutex(true, "Polyscreen_SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("Polyscreen is already running (check the system tray).",
                "Polyscreen", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayContext());
        return 0;
    }
}
