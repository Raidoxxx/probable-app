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
        if (Installer.IsSetup && e.Args.Contains("--update"))
        {
            // Atualização pedida pelo próprio app: troca o executável sem perguntar e reabre.
            try { Updater.RunUpdate(e.Args); Shutdown(0); }
            catch (Exception ex)
            {
                MessageBox.Show("Não foi possível atualizar automaticamente: " + ex.Message + "\n\nO instalador vai abrir para você tentar de novo.", Installer.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
                ShutdownMode = ShutdownMode.OnLastWindowClose; Installer.SetupWindow().Show();
            }
            return;
        }
        if (e.Args.Contains("--update-test")) { _ = UpdateTest(); return; }
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
        if (e.Args.Contains("--art-test")) { ArtTest(); Shutdown(0); return; }
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
    // Quadros da animação das pilastras em vários instantes do ciclo (art-test-preview.png) e o custo por quadro.
    private static void ArtTest()
    {
        const int w = 640, h = 820; double[] times = [0.8, 1.8, 2.8, 3.8, 6.0, 9.6, 10.4, 11.2, 12.2, 14.0];
        var art = new DotArt.ColumnsAnimation(w, h);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 120; i++) art.Render(i / 8.0);
        double ms = watch.Elapsed.TotalMilliseconds / 120;
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(10, 10, 10)), null, new Rect(0, 0, w * 5, h * 2));
            for (int i = 0; i < times.Length; i++)
            {
                art.Render(times[i]);
                var frame = new System.Windows.Media.Imaging.WriteableBitmap(art.Bitmap); frame.Freeze();
                dc.DrawImage(frame, new Rect(i % 5 * w, i / 5 * h, w, h));
                dc.DrawText(new System.Windows.Media.FormattedText($"t={times[i]}s", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new System.Windows.Media.Typeface("Consolas"), 22, System.Windows.Media.Brushes.Orange, 1), new Point(i % 5 * w + 10, i / 5 * h + 10));
            }
        }
        var sheet = new System.Windows.Media.Imaging.RenderTargetBitmap(w * 5, h * 2, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); sheet.Render(visual);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(sheet));
        using (var file = System.IO.File.Create("art-test-preview.png")) png.Save(file);
        System.IO.File.WriteAllText("art-test-result.txt", $"PASS: {ms:F2} ms por quadro ({w}x{h})");
    }
    // Consulta a release real: em dia com a versão atual, e "desatualizado" quando se finge ser 0.0.1 (baixa o instalador).
    private async Task UpdateTest()
    {
        System.IO.File.WriteAllText("update-test-result.txt", "RUNNING");
        try
        {
            var none = await Updater.Check(new Version(999, 0, 0));
            if (none is not null) throw new InvalidOperationException("Versão 999 não deveria ter atualização.");
            var old = await Updater.Check(new Version(0, 0, 1)) ?? throw new InvalidOperationException("Nenhuma release com instalador encontrada em " + Updater.Repo);
            string file = await Updater.Download(old);
            long size = new System.IO.FileInfo(file).Length; System.IO.File.Delete(file);
            if (size != old.Size) throw new InvalidOperationException($"Tamanho baixado {size} != {old.Size}");
            System.IO.File.WriteAllText("update-test-result.txt", $"PASS: repo {Updater.Repo}, atual {Updater.Current}, última {old.Tag} ({old.Size / 1048576.0:F1} MB baixados e conferidos), instalado={Updater.IsInstalled}");
            Shutdown(0);
        }
        catch (Exception ex) { System.IO.File.WriteAllText("update-test-result.txt", ex.ToString()); Shutdown(1); }
    }
    private async Task Integration()
    {
        System.IO.File.WriteAllText("integration-test-result.txt", "RUNNING");
        try { await IntegrationTests.Run(); Shutdown(0); }
        catch (Exception ex) { System.IO.File.WriteAllText("integration-test-result.txt", ex.ToString()); Shutdown(1); }
    }
}
