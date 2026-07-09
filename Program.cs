using System;
using Velopack;

namespace Wisp
{
    /// <summary>
    /// Explicit process entry point. Its whole reason to exist is to run Velopack BEFORE WPF starts:
    /// <see cref="VelopackApp"/>.Run() has to be the very first thing the process does so it can intercept
    /// the Velopack install / update / uninstall hook launches and exit immediately for them, before any
    /// window, theme, or the single-instance lock comes up. Only after that do we spin up the WPF app.
    ///
    /// App.xaml is compiled as a Page (see Wisp.csproj) precisely so WPF does NOT also generate its own
    /// Main() - this is the one and only entry point.
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            // Must be first. No-op for a normal launch and for un-packaged dev runs.
            VelopackApp.Build().Run();

            var app = new App();
            app.InitializeComponent(); // loads App.xaml resources (themes) + ShutdownMode
            app.Run();                 // raises Startup -> App.OnStartup
        }
    }
}
