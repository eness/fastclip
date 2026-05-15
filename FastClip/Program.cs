using System.Windows.Forms;
using FastClip.Application;
using FastClip.Infrastructure;

namespace FastClip;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        System.Windows.Forms.Application.ThreadException += (_, args) => GlobalErrorHandler.Handle("ui-thread", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            GlobalErrorHandler.Handle("appdomain", args.ExceptionObject as Exception ?? new Exception("Unknown unhandled exception."));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            GlobalErrorHandler.Handle("task-scheduler", args.Exception);
            args.SetObserved();
        };

        ApplicationConfiguration.Initialize();
        System.Windows.Forms.Application.Run(new TrayApplicationContext());
    }
}
