using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace PCBoost;

// Instalador por usuário, sem administrador: o mesmo executável, quando se chama "PCBoost-Setup.exe",
// copia-se para %LOCALAPPDATA%\Programs\PCBoost, cria atalhos e registra a desinstalação no Windows.
public static class Installer
{
    public const string AppName = "PCBoost";
    public static string InstallDir => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
    public static string InstalledExe => System.IO.Path.Combine(InstallDir, AppName + ".exe");
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName;
    private static string StartMenuLink => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
    private static string DesktopLink => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    public static bool IsSetup => System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Contains("setup", StringComparison.OrdinalIgnoreCase);

    public static void Install(bool desktopShortcut)
    {
        string source = Environment.ProcessPath ?? throw new InvalidOperationException("Executável não encontrado.");
        Directory.CreateDirectory(InstallDir);
        // Fecha uma versão antiga aberta antes de substituir.
        foreach (var running in Process.GetProcessesByName(AppName))
            using (running)
            {
                try { if (string.Equals(running.MainModule?.FileName, InstalledExe, StringComparison.OrdinalIgnoreCase)) { running.CloseMainWindow(); if (!running.WaitForExit(5000)) throw new InvalidOperationException("Feche o PCBoost aberto e tente de novo."); } }
                catch (System.ComponentModel.Win32Exception) { }
            }
        string temp = InstalledExe + ".new";
        File.Copy(source, temp, true);
        File.Move(temp, InstalledExe, true);
        Shortcut(StartMenuLink);
        if (desktopShortcut) Shortcut(DesktopLink);
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
        key.SetValue("Publisher", AppName);
        key.SetValue("DisplayIcon", InstalledExe);
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        key.SetValue("NoModify", 1); key.SetValue("NoRepair", 1);
        key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024));
    }
    private static void Shortcut(string link)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Não foi possível criar atalhos.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(link);
        shortcut.TargetPath = InstalledExe; shortcut.WorkingDirectory = InstallDir; shortcut.IconLocation = InstalledExe + ",0"; shortcut.Description = "Boost, servidores, chat e voz";
        shortcut.Save();
    }

    public static void Uninstall()
    {
        if (MessageBox.Show("Remover o PCBoost deste PC? Suas configurações locais ficam em %LOCALAPPDATA%\\PCBoost e podem ser apagadas depois.", "Desinstalar PCBoost", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var link in new[] { StartMenuLink, DesktopLink }) try { File.Delete(link); } catch (Exception) { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch (Exception) { }
        // O executável em uso não pode apagar a si mesmo: um cmd espera ele fechar e remove a pasta.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 > nul & rmdir /s /q \"{InstallDir}\"") { CreateNoWindow = true, UseShellExecute = false })?.Dispose();
    }

    // Janela do instalador no mesmo visual do app.
    public static Window SetupWindow()
    {
        var window = new Window { Title = "Instalar PCBoost", Width = 460, Height = 420, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = (Brush)Application.Current.FindResource("Bg"), Foreground = (Brush)Application.Current.FindResource("Ink"), FontFamily = (FontFamily)Application.Current.FindResource("Sans") };
        var logo = new Grid { Width = 58, Height = 58, Margin = new Thickness(0, 34, 0, 26) };
        logo.Children.Add(new Ellipse { Stroke = Brushes.White, StrokeThickness = 4.5 });
        logo.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M10,42 C18,18 38,10 52,17"), Stroke = Brushes.White, StrokeThickness = 4.5 });
        var status = new TextBlock { Text = "Boost de um clique, servidores com chat, voz e quadro de ideias.", Foreground = (Brush)Application.Current.FindResource("Muted"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 22) };
        var desktop = new CheckBox { Content = "Criar atalho na área de trabalho", IsChecked = true, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 22), Foreground = (Brush)Application.Current.FindResource("Muted") };
        var install = new Button { Content = "Instalar", Style = (Style)Application.Current.FindResource("PrimaryButton"), Width = 280, Padding = new Thickness(0, 10, 0, 10), Margin = new Thickness(0), FontSize = 14 };
        var where = new TextBlock { Text = InstallDir, FontFamily = (FontFamily)Application.Current.FindResource("Mono"), FontSize = 10, Foreground = (Brush)Application.Current.FindResource("Dim"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 380 };
        install.Click += async (_, _) =>
        {
            install.IsEnabled = false; install.Content = "Instalando…";
            try
            {
                bool shortcut = desktop.IsChecked == true;
                await Task.Run(() => Install(shortcut));
                Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true, WorkingDirectory = InstallDir })?.Dispose();
                window.Close();
            }
            catch (Exception ex) { status.Text = ex.Message; status.Foreground = (Brush)Application.Current.FindResource("Danger"); install.IsEnabled = true; install.Content = "Tentar de novo"; }
        };
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(logo);
        stack.Children.Add(new TextBlock { Text = "PCBoost", FontSize = 24, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(status); stack.Children.Add(desktop); stack.Children.Add(install); stack.Children.Add(where);
        window.Content = new Border { Margin = new Thickness(14), BorderBrush = (Brush)Application.Current.FindResource("Line"), BorderThickness = new Thickness(1), Background = (Brush)Application.Current.FindResource("Paper"), Child = stack };
        return window;
    }
}
