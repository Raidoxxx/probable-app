using System.Windows;

namespace PCBoost;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = new("pt-BR");
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = new("pt-BR");
        if (e.Args.Contains("--uninstall")) { Installer.Uninstall(); Shutdown(); return; }
        if (Installer.IsSetup && !e.Args.Any(a => a.StartsWith("--")))
        {
            // PCBoost-Setup.exe: instala por usuário (sem administrador) e abre o app instalado.
            ShutdownMode = ShutdownMode.OnLastWindowClose; Installer.SetupWindow().Show(); return;
        }
        if (e.Args.Contains("--integration-test")) { _ = Integration(); return; }
        if (e.Args.Contains("--boost-test"))
        {
            _ = Task.Run(async () =>
            {
                try { System.IO.File.WriteAllText("boost-test-result.txt", await AutoBoostTest.Run()); Dispatcher.Invoke(() => Shutdown(0)); }
                catch (Exception ex) { System.IO.File.WriteAllText("boost-test-result.txt", ex.ToString()); Dispatcher.Invoke(() => Shutdown(1)); }
            });
            return;
        }
        if (e.Args.Contains("--team-test")) { _ = TeamTest(e.Args.SkipWhile(a => a != "--team-test").Skip(1).FirstOrDefault() ?? Team.TeamClient.DefaultServer); return; }
        if (e.Args.Contains("--test-child"))
        {
            Thread.Sleep(TimeSpan.FromSeconds(30));
            Shutdown(); return;
        }
        if (e.Args.Contains("--self-test"))
        {
            try { SelfTest.Run(); Shutdown(0); }
            catch (Exception ex) { System.IO.File.WriteAllText("self-test-result.txt", ex.ToString()); Shutdown(1); }
            return;
        }
        bool uiTest = e.Args.Contains("--ui-test") || e.Args.Contains("--boost-ui-test") || e.Args.Contains("--idle-test");
        var window = new MainWindow(uiTest ? new LocalStore(System.IO.Path.Combine(AppContext.BaseDirectory, "ui-test-state")) : null);
        if (uiTest)
        {
            window.EnableUiTestMode();
            System.IO.File.WriteAllText(e.Args.Contains("--idle-test") ? "idle-test-result.txt" : "ui-test-result.txt", "RUNNING");
            window.Loaded += async (_, _) =>
            {
                try
                {
                    if (e.Args.Contains("--idle-test")) await window.MeasureIdle(); else await window.RunUiTest();
                    if (e.Args.Contains("--boost-ui-test")) System.IO.File.Copy("ui-test-result.txt", "boost-ui-test-result.txt", true);
                    Shutdown(0);
                }
                catch (Exception ex) { System.IO.File.WriteAllText(e.Args.Contains("--idle-test") ? "idle-test-result.txt" : "ui-test-result.txt", ex.ToString()); Shutdown(1); }
            };
        }
        window.Show();
    }
    private async Task TeamTest(string server)
    {
        System.IO.File.WriteAllText("team-test-result.txt", "RUNNING");
        try { System.IO.File.WriteAllText("team-test-result.txt", await Team.TeamTests.Run(server)); Shutdown(0); }
        catch (Exception ex) { System.IO.File.WriteAllText("team-test-result.txt", ex.ToString()); Shutdown(1); }
    }
    private async Task Integration()
    {
        System.IO.File.WriteAllText("integration-test-result.txt", "RUNNING");
        try { await IntegrationTests.Run(); Shutdown(0); }
        catch (Exception ex) { System.IO.File.WriteAllText("integration-test-result.txt", ex.ToString()); Shutdown(1); }
    }
}
