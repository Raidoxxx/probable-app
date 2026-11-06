using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace PCBoost;

public partial class MainWindow : Window
{
    private readonly LocalStore store;
    private readonly BoosterSettings settings;
    private readonly TelemetryService telemetry;
    private readonly DispatcherTimer timer = new();
    private readonly DispatcherTimer logTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Dictionary<string, TextBox> fields = [];
    private MemorySample? latest;
    private List<ProcessRow> processes = [];
    private MinecraftSession? session;
    private Task? sessionTask;
    private OwnedProcess? game;
    private SessionLog? gameLog;
    private bool sampling, busy, closed, sidebarCollapsed;
    private bool measureForeground;
    private bool groupChildren = true;
    private int page;
    private TextBlock? availableMetric, usedMetric, commitMetric, alerts, poolDetails, processSampleLabel, profileReport, preflightText, sessionState;
    private UniformGrid? metricsGrid;
    private Canvas? chart;
    private DataGrid? processGrid, rulesGrid, reviewGrid, stageGrid;
    private TextBox? sessionOutput;
    private ComboBox? projectSelector, artifactSelector;
    private Button? runButton, forceButton, proceedButton;
    private Panel? memoryActions, artifactActions;
    private List<RuleEditorRow> ruleRows = [];
    private List<ProfileCandidate> candidates = [];
    private long logVersion = -1;
    private DateTime gridSampleShown;
    private bool gridGroupingShown;
    private int stageCountShown = -1;
    private const int PageOverview = 0, PageDiagnosis = 1, PageGames = 2, PageWork = 3, PageDev = 4, PageHistory = 5, PageGroups = 6, PageSettings = 7, PageMonitor = 8;
    // No menu só aparecem Boost, Grupos e Configurações; o resto fica em Configurações → Avançado.
    // Configurações fica na engrenagem ao lado da conta, não no menu.
    private static readonly HashSet<int> MenuPages = [PageOverview, PageGroups];
    private readonly string[] titles = ["Boost", "Diagnóstico", "Perfil de jogo manual", "Modo trabalho", "Desenvolvimento", "Histórico", "Servidores", "Configurações", "Monitor de memória"];
    private readonly string[] icons = ["◆", "⌕", "◇", "▤", "⌘", "◷", "◎", "⚙", "◫"];
    private readonly (string Section, int First)[] sections = [("BOOST", PageOverview), ("SERVIDORES", PageGroups), ("SISTEMA", PageSettings)];
    private ProjectConfig? editing;
    private Image? sphere;
    private Canvas?[] sparks = new Canvas?[3];
    private double sphereAngle;
    private bool uiTestMode;

    public MainWindow(LocalStore? localStore = null)
    {
        store = localStore ?? new LocalStore(); settings = store.Load(); telemetry = new(store, settings);
        team = new Team.TeamClient(store.Root);
        InitializeComponent();
        InitTeam(); InitBoost();
        toastTimer.Tick += (_, _) => { toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
        Navigate(0);
        SourceInitialized += (_, _) => DarkTitleBar();
        Loaded += async (_, _) => { await Collect(true); timer.Start(); logTimer.Start(); if (store.Warning is not null) Status(store.Warning); if (!uiTestMode) await ResumeLogin(); };
        timer.Tick += async (_, _) => await Collect(false);
        logTimer.Tick += (_, _) => UpdateSessionWidgets();
        Closing += OnClosing;
        Closed += (_, _) => { closed = true; timer.Stop(); logTimer.Stop(); try { telemetry.Flush(); session?.Dispose(); game?.Dispose(); gameLog?.Dispose(); ShutdownTeam(); } catch (Exception) { } };
    }
    internal void EnableUiTestMode() => uiTestMode = true;
    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    private void DarkTitleBar()
    {
        try { int on = 1; DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(this).Handle, 20, ref on, sizeof(int)); } catch (Exception) { }
    }
    private void Status(string text)
    {
        if (closed) return;
        // Avisos aparecem como um toast discreto no canto, como no Discord.
        StatusLabel.Text = text.Length > 400 ? text[..400] + "…" : text;
        Toast.Visibility = Visibility.Visible; toastTimer.Stop(); toastTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(text.Length / 18.0, 3, 9)); toastTimer.Start();
    }
    private readonly DispatcherTimer toastTimer = new();
    private async Task Execute(Func<Task> action)
    {
        if (busy) return; busy = true; ActivityBar.IsIndeterminate = true; ActivityBar.Visibility = Visibility.Visible;
        try { await action(); }
        catch (Exception ex) { Status(ex.Message); }
        finally { busy = false; ActivityBar.IsIndeterminate = false; ActivityBar.Visibility = Visibility.Collapsed; UpdateSessionWidgets(); }
    }
    private async Task Collect(bool force)
    {
        if (sampling || closed) return; sampling = true;
        try
        {
            bool active = force || measureForeground || IsActive && WindowState != WindowState.Minimized;
            latest = await Task.Run(() => telemetry.Sample(active));
            if (session?.Running == true) session.RecordMemory(latest);
            processes = telemetry.Processes.Select(p => p.Process).ToList();
            bool pressure = latest.Available < settings.LowAvailableMb * 1048576L || latest.Committed is long c && latest.CommitLimit is > 0 && 100d * c / latest.CommitLimit >= settings.CommitPercent;
            CollectionState.Text = latest.TimeLabel + (pressure ? " · PRESSÃO DE MEMÓRIA" : " · COLETA ATIVA");
            CollectionState.Foreground = pressure ? Brush("#E3A33B") : Brush("#8C8C8C");
            timer.Interval = TimeSpan.FromSeconds(active ? settings.SampleSeconds : settings.BackgroundSeconds);
            UpdateDashboard();
            session?.ReleaseLockIfStopped();
        }
        catch (Exception ex) { CollectionState.Text = "Coleta indisponível"; Status(ex.Message); }
        finally { sampling = false; }
    }
    private static TextBlock Text(string text, double size = 14, bool bold = false, string? color = null) => new()
    { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap,
        Foreground = Brush(color ?? "#EDEDED") };
    private static TextBlock Muted(string text, double size = 12) => Text(text, size, false, "#8C8C8C");
    private static TextBlock Mono(string text, double size = 11, string color = "#8C8C8C") => new()
    { Text = text, FontSize = size, FontFamily = (FontFamily)Application.Current.FindResource("Mono"), Foreground = Brush(color), TextWrapping = TextWrapping.Wrap };
    private static StackPanel Stack(params UIElement[] children) { var panel = new StackPanel(); foreach (var child in children) panel.Children.Add(child); return panel; }
    private Button Button(string label, Action action, bool primary = false)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8) };
        if (primary) button.Style = (Style)Application.Current.FindResource("PrimaryButton");
        button.Click += (_, _) => { try { action(); } catch (Exception ex) { Status(ex.Message); } }; return button;
    }
    private static Border Card(UIElement content, bool dark = false) => new()
    { Child = content, Background = dark ? Brushes.Black : Brush("#101010"),
        BorderBrush = Brush(dark ? "#333333" : "#262626"), BorderThickness = new Thickness(1), Padding = new Thickness(22), Margin = new Thickness(0, 0, 0, 16) };
    private static WrapPanel Actions(params Button[] buttons) { var panel = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) }; foreach (var button in buttons) panel.Children.Add(button); return panel; }
    private static DataGrid Table(params (string Header, string Binding, double Width)[] columns)
    {
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, MaxHeight = 330, MinHeight = 100 };
        foreach (var (title, binding, width) in columns) grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(binding), Width = width == 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width) });
        return grid;
    }
    private void Navigate(int index)
    {
        page = index; fields.Clear(); PageHost.Children.Clear();
        gridSampleShown = default; stageCountShown = -1;
        candidates = [];
        availableMetric = usedMetric = commitMetric = alerts = poolDetails = processSampleLabel = profileReport = preflightText = sessionState = null;
        metricsGrid = null; chart = null; processGrid = rulesGrid = reviewGrid = stageGrid = null;
        sessionOutput = null; artifactSelector = projectSelector = null; forceButton = runButton = proceedButton = null; memoryActions = artifactActions = null;
        sphere = null; sparks = new Canvas?[3]; ClearTeamView();
        bool full = index == PageGroups;
        PageScroll.Visibility = full ? Visibility.Collapsed : Visibility.Visible; FullHost.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        PageEyebrow.Text = icons[index]; PageEyebrow.FontFamily = (FontFamily)FindResource("Sans"); HeaderActions.Children.Clear();
        PageTitle.Text = titles[index]; PageSubtitle.Text = index switch
        { PageOverview => "Um clique para liberar o PC. Clique em um jogo para dar boost e abrir.", PageMonitor => "Memória, consumo e histórico recente do seu PC.", PageDiagnosis => "Memória física, comprometimento e tendências reais.", PageGames => "Revise os apps, prepare a sessão e abra o jogo.",
          PageWork => "Feche distrações e mantenha ferramentas de trabalho protegidas.", PageDev => "Build, testes e servidor Minecraft em etapas, com diagnóstico.", PageHistory => "Resultados e medições salvos neste PC.",
          PageGroups => "Converse, entre em chamada e organize ideias e tarefas com seu servidor.", _ => "Regras, coleta e conta sob seu controle." };
        SettingsButton.Background = index == PageSettings ? Brush("#262626") : Brushes.Transparent;
        switch (index)
        {
            case PageOverview: BoostPage(); break; case PageMonitor: Dashboard(); break; case PageDiagnosis: Diagnosis(); break; case PageGames: Games(); break; case PageWork: WorkPage(); break;
            case PageDev: Development(); break; case PageHistory: History(); break; case PageGroups: GroupsPage(); break; case PageSettings: SettingsPage(); break;
        }
        RenderRail(); RenderSidebar();
        UpdateDashboard(); UpdateSessionWidgets(); ApplyLayout();
    }
    private void Dashboard()
    {
        // Painel principal: esfera de pontos que "enche" com a RAM em uso + células de métricas com sparklines.
        var hero = new Grid(); hero.RowDefinitions.Add(new() { Height = GridLength.Auto }); hero.RowDefinitions.Add(new()); hero.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var head = new DockPanel { Margin = new Thickness(20, 16, 20, 0) };
        var today = Mono(DateTime.Now.ToString("dd MMM yyyy").ToUpperInvariant(), 11, "#EDEDED"); DockPanel.SetDock(today, Dock.Right); head.Children.Add(today);
        var left = new StackPanel();
        var line1 = new TextBlock { FontFamily = (FontFamily)FindResource("Mono"), FontSize = 11 };
        line1.Inlines.Add(new System.Windows.Documents.Run("SEU PC • ") { Foreground = Brush("#EDEDED") });
        line1.Inlines.Add(new System.Windows.Documents.Run("MONITORANDO") { Foreground = (Brush)FindResource("Accent") });
        left.Children.Add(line1); left.Children.Add(Mono($"MÁQUINA: {Environment.MachineName.ToUpperInvariant()} · {Environment.ProcessorCount} NÚCLEOS LÓGICOS", 10.5, "#5C5C5C"));
        head.Children.Add(left); hero.Children.Add(head);
        sphere = new Image { Height = 300, Stretch = Stretch.None, Margin = new Thickness(0, 6, 0, 6) };
        sphere.SizeChanged += (_, _) => RenderSphere(false);
        Grid.SetRow(sphere, 1); hero.Children.Add(sphere);
        metricsGrid = new UniformGrid { Columns = ActualWidth < 1120 ? 1 : 3 };
        Border Metric(int slot, string title, out TextBlock value, string hint)
        {
            value = Text("—", 22, true); value.Margin = new Thickness(0, 6, 0, 2); value.FontFamily = (FontFamily)FindResource("Mono");
            sparks[slot] = new Canvas { Height = 34, ClipToBounds = true, Margin = new Thickness(0, 8, 0, 0) };
            var canvas = sparks[slot]!; canvas.SizeChanged += (_, _) => DrawSparks();
            return new Border { BorderBrush = Brush("#262626"), BorderThickness = new Thickness(0, 1, slot < 2 ? 1 : 0, 0), Padding = new Thickness(16, 12, 16, 12),
                Child = Stack(Mono(title, 10.5, "#EDEDED"), Mono(hint, 10, "#5C5C5C"), value, canvas) };
        }
        metricsGrid.Children.Add(Metric(0, "RAM DISPONÍVEL", out availableMetric, "INCLUI MEMÓRIA REUTILIZÁVEL"));
        metricsGrid.Children.Add(Metric(1, "RAM EM USO", out usedMetric, "SISTEMA INTEIRO, COM KERNEL"));
        metricsGrid.Children.Add(Metric(2, "COMPROMETIDA", out commitMetric, "USO / LIMITE · NÃO É RAM RESIDENTE"));
        Grid.SetRow(metricsGrid, 2); hero.Children.Add(metricsGrid);
        PageHost.Children.Add(new Border { Background = Brushes.Black, BorderBrush = Brush("#262626"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 16), Child = hero });

        chart = new Canvas { Height = 170, ClipToBounds = true, Background = Brushes.Transparent };
        chart.SizeChanged += (_, _) => DrawChart();
        PageHost.Children.Add(Card(Stack(Mono("MEMÓRIA AO LONGO DO TEMPO", 11, "#EDEDED"), Mono("RAM EM USO — LINHA CONTÍNUA · COMPROMETIMENTO — TRACEJADA · ÚLTIMAS 120 AMOSTRAS", 10, "#5C5C5C"), chart)));
        var callout = Stack(Text("Prepare o que você vai fazer agora.", 22, true, "#FFFFFF"),
            Text("Diagnostique a memória, libere o PC para jogar ou trabalhar, e chame seu servidor.", 13, false, "#8C8C8C"),
            Actions(Button("Preparar para jogar", () => Navigate(PageGames), true), Button("Modo trabalho", () => Navigate(PageWork)), Button("Diagnosticar memória", () => Navigate(PageDiagnosis)),
                Button("Testar plugin", () => Navigate(PageDev)), Button("Abrir servidores", () => Navigate(PageGroups))));
        PageHost.Children.Add(Card(callout, true));
        processGrid = Table(("PROCESSO", "Name", 0), ("PID", "Pid", 70), ("RAM PRIVADA", "Resident", 140), ("COMPROMETIDA", "Commit", 145), ("VARIAÇÃO", "Change", 110));
        processSampleLabel = Mono("", 10, "#5C5C5C");
        PageHost.Children.Add(Card(Stack(Mono("MAIORES CONSUMIDORES", 11, "#EDEDED"), processSampleLabel, processGrid)));
    }
    private void RenderSphere(bool advance)
    {
        if (sphere is null || latest is null || sphere.ActualWidth < 10 && ActualWidth < 10) return;
        if (advance) sphereAngle += 0.12;
        int width = (int)Math.Max(100, (sphere.Parent as FrameworkElement)?.ActualWidth ?? 600);
        sphere.Source = DotArt.Sphere(width, 300, sphereAngle, latest.Total > 0 ? (double)latest.Used / latest.Total : 0);
    }
    private void DrawSparks()
    {
        var points = telemetry.Samples.TakeLast(90).ToList();
        Func<MemorySample, double?>[] series = [s => s.Available, s => s.Used, s => s.Committed];
        for (int k = 0; k < 3; k++)
        {
            var canvas = sparks[k]; if (canvas is null) continue; canvas.Children.Clear();
            double width = canvas.ActualWidth, height = canvas.ActualHeight; if (width < 2 || points.Count < 2) continue;
            var values = points.Select(series[k]).ToList(); var known = values.Where(v => v is not null).Select(v => v!.Value).ToList(); if (known.Count < 2) continue;
            double min = known.Min(), max = known.Max(), range = Math.Max(1, max - min) * 1.15;
            var line = new Polyline { Stroke = Brush("#D8D8D8"), StrokeThickness = 1 };
            for (int i = 0; i < values.Count; i++) if (values[i] is double v) line.Points.Add(new(width * i / (values.Count - 1), height - 3 - (height - 6) * (v - min) / range));
            canvas.Children.Add(line);
            if (line.Points.Count > 0)
            {
                var end = line.Points[^1]; var marker = new Rectangle { Width = 5, Height = 5, Fill = Brushes.White };
                Canvas.SetLeft(marker, end.X - 5); Canvas.SetTop(marker, end.Y - 2.5); canvas.Children.Add(marker);
            }
        }
    }
    private void DrawChart()
    {
        if (chart is null) return; chart.Children.Clear(); double width = chart.ActualWidth, height = chart.ActualHeight - 24;
        if (width < 1) return;
        for (int i = 0; i <= 4; i++) chart.Children.Add(new Line { X1 = 0, X2 = width, Y1 = i * height / 4 + 8, Y2 = i * height / 4 + 8, Stroke = Brush("#1C1C1C"), StrokeThickness = 1 });
        var points = telemetry.Samples.TakeLast(120).ToList(); if (points.Count == 0) return;
        var ram = new Polyline { Stroke = Brush("#EDEDED"), StrokeThickness = 1.4 };
        var commit = new Polyline { Stroke = Brush("#6A6A6A"), StrokeThickness = 1.1, StrokeDashArray = new DoubleCollection { 3, 3 } };
        double span = Math.Max(1, (points[^1].At - points[0].At).TotalSeconds);
        foreach (var sample in points)
        {
            double x = width * (sample.At - points[0].At).TotalSeconds / span;
            ram.Points.Add(new(x, 8 + height * (1 - Math.Clamp((double)sample.Used / sample.Total, 0, 1))));
            if (sample.Committed is long c && sample.CommitLimit is > 0) commit.Points.Add(new(x, 8 + height * (1 - Math.Clamp((double)c / sample.CommitLimit.Value, 0, 1))));
        }
        chart.Children.Add(ram); chart.Children.Add(commit);
        if (ram.Points.Count > 0) { var marker = new Rectangle { Width = 6, Height = 6, Fill = Brushes.White }; Canvas.SetLeft(marker, ram.Points[^1].X - 6); Canvas.SetTop(marker, ram.Points[^1].Y - 3); chart.Children.Add(marker); }
        var label = Mono(points[0].TimeLabel + "  —  " + points[^1].TimeLabel, 10, "#5C5C5C"); Canvas.SetTop(label, height + 10); chart.Children.Add(label);
    }
    private void UpdateDashboard()
    {
        if (latest is null) return;
        if (availableMetric is not null) availableMetric.Text = FileCleaner.FormatSize(latest.Available);
        if (usedMetric is not null) usedMetric.Text = FileCleaner.FormatSize(latest.Used);
        if (commitMetric is not null) commitMetric.Text = latest.Committed is long c && latest.CommitLimit is long l
            ? $"{c / 1073741824d:N1} / {l / 1073741824d:N1} GB" : "Indisponível";
        if (poolDetails is not null) poolDetails.Text = $"Pool paginável: {Format(latest.PagedPool)}\nPool não paginável: {Format(latest.NonpagedPool)}\nCache do sistema (GetPerformanceInfo): {Format(latest.Cache)}\nEm espera: indisponível nesta fonte\nReservada para hardware: indisponível nesta fonte";
        if (processGrid is not null && (gridSampleShown != telemetry.ProcessReadAt || gridGroupingShown != groupChildren))
        {
            processGrid.ItemsSource = groupChildren ? ProcessFamily.Group(telemetry.Processes).Take(page == PageMonitor ? 12 : 500).ToList()
                : telemetry.Processes.OrderByDescending(p => p.Process.PrivateBytes ?? -1).Take(page == PageMonitor ? 12 : 500).ToList();
            gridSampleShown = telemetry.ProcessReadAt; gridGroupingShown = groupChildren;
        }
        if (alerts is not null)
        {
            var notices = MemoryDiagnosis.Evaluate(telemetry.Samples, settings);
            long visiblePrivate = telemetry.Processes.Sum(p => p.Process.PrivateBytes ?? 0);
            if (telemetry.Processes.Count > 0 && visiblePrivate < latest.Used * 0.6)
                notices.Add($"As leituras privadas disponíveis somam {FileCleaner.FormatSize(visiblePrivate)} de {FileCleaner.FormatSize(latest.Used)} em uso. Examine pools e cache abaixo e a orientação RAMMap; a diferença também inclui páginas compartilhadas e processos sem leitura.");
            alerts.Text = string.Join("\n\n", notices);
        }
        if (processSampleLabel is not null) processSampleLabel.Text = $"Processos lidos às {telemetry.ProcessReadAt.ToLocalTime():HH:mm:ss}; intervalo mínimo {settings.ProcessSeconds}s. Variação entre leituras. Campos indisponíveis não são zero.";
        DrawChart(); DrawSparks(); RenderSphere(true);
    }
    private static string Format(long? bytes) => bytes is long value ? FileCleaner.FormatSize(value) : "Indisponível";
    private void Diagnosis()
    {
        alerts = Text("Coletando evidências…", 16);
        PageHost.Children.Add(Card(Stack(Text("Alertas explicáveis", 20, true), alerts)));
        var sample = latest;
        poolDetails = Text("Coletando pools e cache…", 15);
        PageHost.Children.Add(Card(Stack(Text("Além dos processos", 20, true),
            poolDetails,
            Muted("A diferença entre RAM em uso e RAM privada dos processos também inclui páginas compartilhadas, kernel e processos sem leitura. Crescimento persistente é um indício, não confirmação de vazamento."),
            Actions(Button("Orientação sobre RAMMap ↗", () => Process.Start(new ProcessStartInfo("https://learn.microsoft.com/en-us/sysinternals/downloads/rammap") { UseShellExecute = true })), Button("Atualizar diagnóstico", () => _ = Execute(async () => { await Collect(true); Navigate(PageDiagnosis); }))))));
        processGrid = Table(("PROCESSO", "Name", 0), ("PID", "Pid", 70), ("PAI", "Parent", 70), ("RAM PRIVADA", "Resident", 140), ("COMPROMETIDA", "Commit", 145), ("VARIAÇÃO", "Change", 100));
        processSampleLabel = Muted("");
        var grouping = new CheckBox { Content = "Agrupar filhos quando a relação e a identidade do pai forem conhecidas", IsChecked = groupChildren, Margin = new Thickness(0, 12, 0, 12) };
        grouping.Checked += (_, _) => { groupChildren = true; UpdateDashboard(); }; grouping.Unchecked += (_, _) => { groupChildren = false; UpdateDashboard(); };
        PageHost.Children.Add(Card(Stack(Text("Processos · leitura independente do idioma do Windows", 18, true),
            Muted("PID pai é exibido quando conhecido. RAM privada residente difere da memória privada comprometida."), processSampleLabel, grouping, processGrid)));
    }
    private TextBox Field(StackPanel panel, string key, string label, string value = "", Action<TextBox>? browse = null)
    {
        var text = new TextBox { Text = value, MinWidth = 100, Margin = new Thickness(0, 6, 0, 14) }; fields[key] = text;
        panel.Children.Add(Muted(label, 12));
        if (browse is null) panel.Children.Add(text);
        else
        {
            var grid = new Grid(); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            grid.Children.Add(text); var button = Button("…", () => browse(text)); button.Margin = new Thickness(8, 6, 0, 14); Grid.SetColumn(button, 1); grid.Children.Add(button); panel.Children.Add(grid);
        }
        return text;
    }
    private static void PickFolder(TextBox target) { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog() == true) target.Text = dialog.FolderName; }
    private static void PickFile(TextBox target, string filter = "Executáveis|*.exe;*.cmd;*.bat|Todos|*.*") { var dialog = new OpenFileDialog { Filter = filter }; if (dialog.ShowDialog() == true) target.Text = dialog.FileName; }
    private string Value(string key) => fields[key].Text.Trim();
    private int Number(string key) => int.TryParse(Value(key), out int result) ? result : throw new InvalidDataException("Valor numérico inválido: " + key);
    private void Games()
    {
        var form = new StackPanel(); Field(form, "game", "Jogo ou launcher", settings.Game.Launch.Executable, box => PickFile(box));
        Field(form, "gameArgs", "Argumentos (lista JSON)", JsonSerializer.Serialize(settings.Game.Launch.Arguments));
        form.Children.Add(Actions(Button("Salvar jogo", SaveGame), Button("Preparar e abrir jogo", () => _ = Execute(PrepareGame), true),
            Button("Parar sessão", () => { game?.RequestStop(); Status("Parada normal solicitada ao jogo/launcher iniciado pelo app."); }),
            Button("Confirmar parada forçada", () => { if (game is null || !game.StopRequested) throw new InvalidOperationException("Solicite primeiro a parada normal."); game.ForceAfterConfirmation(); Status("Parada forçada confirmada apenas para a sessão de jogo iniciada aqui."); })));
        PageHost.Children.Add(Card(form)); BuildProfile(false);
    }
    private void SaveGame()
    {
            var command = new CommandSpec { Executable = Value("game"), Arguments = JsonSerializer.Deserialize<List<string>>(Value("gameArgs")) ?? [] };
            SafePaths.Command(command, Path.GetDirectoryName(Path.GetFullPath(command.Executable))!);
            command.Executable = SafePaths.Executable(command.Executable, Path.GetDirectoryName(Path.GetFullPath(command.Executable))!);
            settings.Game.Launch = command; store.Save(settings); Status("Jogo salvo. As regras continuam preservando tudo por padrão.");
    }
    private async Task PrepareGame()
    {
        SaveGame(); if (string.IsNullOrWhiteSpace(settings.Game.Launch.Executable)) return;
        if (game?.HasActiveProcesses == true) throw new InvalidOperationException("Uma sessão de jogo iniciada aqui ainda está ativa.");
        await ApplyProfile(false);
        var previousGame = game; game = null; previousGame?.Dispose(); gameLog?.Dispose(); gameLog = new(store);
        try { game = OwnedProcess.Start(settings.Game.Launch, Path.GetDirectoryName(settings.Game.Launch.Executable)!, "Jogo", gameLog.Add); }
        catch { gameLog.Dispose(); gameLog = null; throw; }
        store.Record(new(DateTime.UtcNow, "Jogos", "Launcher/jogo iniciado", settings.Game.Launch.Executable)); Status("Jogo/launcher iniciado. O app só gerencia os processos vinculados ao job criado nesta sessão.");
    }
    private void BuildProfile(bool development)
    {
        var rules = development ? settings.DevelopmentRules : settings.Game.Rules;
        ruleRows = processes.Where(p => p.Path is not null && !p.Protected && !BoostScanner.IsSensitive(p.Name)).GroupBy(p => p.Path!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new RuleEditorRow { Name = g.First().Name, Path = g.Key, Memory = g.All(p => p.PrivateBytes is null) ? "Indisponível" : (g.Any(p => p.PrivateBytes is null) ? "≥ " : "") + FileCleaner.FormatSize(g.Sum(p => p.PrivateBytes ?? 0)), Action = rules.FirstOrDefault(r => string.Equals(r.Path, g.Key, StringComparison.OrdinalIgnoreCase))?.Action ?? ProfileAction.Preservar }).ToList();
        rulesGrid = Table(("APLICATIVO", "Name", 150), ("EXECUTÁVEL", "Path", 0), ("RAM PRIVADA", "Memory", 130)); rulesGrid.IsReadOnly = false;
        foreach (var column in rulesGrid.Columns) column.IsReadOnly = true;
        rulesGrid.Columns.Add(new DataGridComboBoxColumn { Header = "REGRA", ItemsSource = Enum.GetValues<ProfileAction>(), SelectedItemBinding = new Binding("Action") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 200 });
        rulesGrid.ItemsSource = ruleRows;
        var expand = new Expander { Header = "Regras editáveis · padrão: preservar todos", Content = rulesGrid, Margin = new Thickness(0, 10, 0, 8) };
        reviewGrid = Table(("APP", "Name", 120), ("PID", "Pid", 65), ("RAM", "Memory", 100), ("COMPROMETIDA", "Commit", 125), ("INÍCIO", "Started", 115), ("EXECUTÁVEL", "Path", 0));
        reviewGrid.IsReadOnly = false; foreach (var column in reviewGrid.Columns) column.IsReadOnly = true;
        reviewGrid.Columns.Insert(0, new DataGridCheckBoxColumn { Header = "FECHAR", Binding = new Binding("Selected") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 70 });
        reviewGrid.ItemsSource = candidates;
        profileReport = Muted("Nenhum app será fechado sem uma regra e seleção explícita.");
        PageHost.Children.Add(Card(Stack(Text("Revisar preparação", 20, true), Muted(development ? "IDE, terminais, agentes e processos da sessão/projeto são preservados. Serviços, segurança e Windows também." : "O jogo/launcher, processos associados e apps marcados como preservar ficam protegidos."), expand,
            Actions(Button("Salvar regras e revisar candidatos", () => _ = Execute(async () => { SaveRules(development); await ReviewProfile(development); })),
                Button("Aplicar somente os marcados", () => _ = Execute(() => ApplyProfile(development)))), reviewGrid, profileReport)));
    }
    private void SaveRules(bool dev)
    {
        rulesGrid?.CommitEdit(); rulesGrid?.CommitEdit(DataGridEditingUnit.Row, true);
        var oldRules = dev ? settings.DevelopmentRules : settings.Game.Rules;
        var rules = oldRules.Where(r => !ruleRows.Any(row => string.Equals(row.Path, r.Path, StringComparison.OrdinalIgnoreCase))).Concat(ruleRows.Select(r => new ProfileRule { Path = r.Path, Action = r.Action })).ToList();
        if (dev) settings.DevelopmentRules = rules; else settings.Game.Rules = rules;
        store.Save(settings);
    }
    private async Task ReviewProfile(bool dev)
    {
        var rows = await Task.Run(() => Monitor.Capture(new HashSet<string>()).Processes);
        candidates = ProfileService.Review(rows, dev ? settings.DevelopmentRules : settings.Game.Rules, dev,
            dev ? null : settings.Game.Launch.Executable, OwnedRootIds());
        if (reviewGrid is not null) reviewGrid.ItemsSource = candidates;
        if (profileReport is not null) profileReport.Text = candidates.Count == 0 ? "Nenhum candidato autorizado. Tudo foi preservado." : $"{candidates.Count} candidatos. Marque cada app que pode receber pedido de fechamento normal.";
    }
    private async Task ApplyProfile(bool dev)
    {
        SaveRules(dev);
        reviewGrid?.CommitEdit(); reviewGrid?.CommitEdit(DataGridEditingUnit.Row, true);
        var result = await ProfileService.ApplyAsync(candidates.ToList(), () =>
        {
            var rows = Monitor.Capture(new HashSet<string>()).Processes;
            var allowed = ProfileService.Review(rows, dev ? settings.DevelopmentRules : settings.Game.Rules, dev, dev ? null : settings.Game.Launch.Executable, OwnedRootIds()).Select(c => c.Pid).ToHashSet();
            return rows.Where(p => !allowed.Contains(p.Id)).Select(p => p.Id).ToHashSet();
        });
        string report = $"Antes {result.Before.TimeLabel}: {FileCleaner.FormatSize(result.Before.Available)} disponíveis. Depois {result.After.TimeLabel}: {FileCleaner.FormatSize(result.After.Available)}. Variação observada {(result.After.Available - result.Before.Available) / 1048576d:+0.0;-0.0;0} MB; não é ganho atribuído exclusivamente aos apps.\n" + string.Join("\n", result.Actions);
        if (profileReport is not null) profileReport.Text = report; Status(report);
        store.Record(new(DateTime.UtcNow, dev ? "Perfil Desenvolvimento" : "Perfil Jogos", "Revisão aplicada", report));
    }
    private IEnumerable<int> OwnedRootIds() => (session?.OwnedIds ?? []).Concat(game is null ? [] : new[] { game.Process.Id });

    private void WorkPage()
    {
        PageHost.Children.Add(Card(Stack(Mono("MODO TRABALHO", 11, "#EDEDED"), Text("Libere memória para trabalhar sem perder nada.", 18, true),
            Muted("Marque apps que distraem ou pesam (jogos, launchers, players). IDE, terminais, agentes, Windows, segurança e o próprio PCBoost ficam protegidos. As regras são as mesmas do perfil Desenvolvimento e nada é fechado sem a sua seleção explícita."),
            Actions(Button("Abrir servidores", () => Navigate(PageGroups)), Button("Diagnosticar memória", () => Navigate(PageDiagnosis))))));
        BuildProfile(true);
    }
    private void Development()
    {
        projectSelector = new ComboBox { ItemsSource = settings.Projects, DisplayMemberPath = "Name", Margin = new Thickness(0, 0, 0, 14), MinHeight = 34 };
        projectSelector.SelectedItem = editing ?? settings.Projects.FirstOrDefault();
        editing = projectSelector.SelectedItem as ProjectConfig ?? new ProjectConfig { Java = DefaultJava() };
        projectSelector.SelectionChanged += (_, _) => { if (projectSelector.SelectedItem is ProjectConfig selected) { editing = selected; Navigate(PageDev); } };
        var newProject = Button("Novo projeto", () => { editing = new ProjectConfig { Java = DefaultJava() }; Navigate(PageDev); }); newProject.HorizontalAlignment = HorizontalAlignment.Left; PageHost.Children.Add(newProject);
        PageHost.Children.Add(projectSelector);
        var form = new StackPanel();
        Field(form, "name", "Nome do projeto", editing.Name);
        Field(form, "folder", "Pasta do projeto Java (Gradle/Maven)", editing.Folder, PickFolder);
        Field(form, "java", "java.exe do JDK", editing.Java, box => PickFile(box, "Java|java.exe|Executáveis|*.exe"));
        Field(form, "server", "Pasta dedicada do servidor de teste", editing.ServerFolder, PickFolder);
        Field(form, "serverJar", "JAR do servidor (Paper, PowerNukkitX ou outro compatível)", editing.ServerJar, box => PickFile(box, "Java JAR|*.jar"));
        Field(form, "serverArgs", "Argumentos do servidor · lista JSON", JsonSerializer.Serialize(editing.ServerArguments));
        Field(form, "buildExe", "Executável do build", editing.Build.Executable);
        Field(form, "buildArgs", "Argumentos de build · lista JSON", JsonSerializer.Serialize(editing.Build.Arguments));
        Field(form, "testExe", "Executável dos testes", editing.Tests.Executable);
        Field(form, "testArgs", "Argumentos dos testes · lista JSON", JsonSerializer.Serialize(editing.Tests.Arguments));
        Field(form, "artifact", "JAR do plugin · vazio para detectar após o build", editing.Artifact, box => PickFile(box, "Java JAR|*.jar"));
        Field(form, "pluginFile", "Nome do arquivo no servidor", editing.PluginFileName);
        Field(form, "xms", "Heap inicial -Xms (MB)", editing.XmsMb.ToString());
        Field(form, "xmx", "Heap máximo -Xmx (MB)", editing.XmxMb.ToString());
        Field(form, "nativeMargin", "Margem fora do heap (MB)", editing.NativeMarginMb.ToString());
        Field(form, "buildMargin", "RAM disponível mínima antes do build (MB)", editing.BuildAvailableMb.ToString());
        Field(form, "commitMargin", "Margem mínima de comprometimento (MB)", editing.CommitMarginMb.ToString());
        Field(form, "ports", "Portas necessárias", editing.Ports);
        Field(form, "ready", "Padrão regex de servidor pronto", editing.ReadyPattern);
        Field(form, "pluginPattern", "Padrão regex que confirma este plugin carregado · vazio = inconclusivo", editing.PluginPattern);
        Field(form, "testsPattern", "Padrão de testes aprovados · vazio = buscar relatórios JUnit XML", editing.TestPassPattern);
        Field(form, "timeout", "Tempo para confirmar prontidão (segundos)", editing.ReadyTimeoutSeconds.ToString());
        form.Children.Add(Actions(Button("Detectar Gradle / Maven", DetectProject), Button("Salvar projeto", SaveProject)));
        PageHost.Children.Add(Card(new Expander { Header = "Cadastrar / editar projeto e ambiente de teste", IsExpanded = string.IsNullOrWhiteSpace(editing.Folder), Content = form }));
        preflightText = Muted("Checar valida ambiente, portas e margem de memória antes de executar.");
        runButton = Button("Testar plugin", () => _ = Execute(() => StartProject(false)), true);
        proceedButton = Button("Prosseguir mesmo assim", () => _ = Execute(() => StartProject(true))); proceedButton.IsEnabled = false;
        PageHost.Children.Add(Card(Stack(Text("Checagem e execução", 20, true), preflightText,
            Actions(Button("Checar / tentar de novo", () => _ = Execute(CheckProject)), runButton, proceedButton,
                Button("Ver apps candidatos", () => _ = Execute(async () => { SaveRules(true); await ReviewProfile(true); }))))));
        sessionState = Text(session?.State ?? "Nenhuma sessão em andamento.", 16, true);
        stageGrid = Table(("ETAPA", "Stage", 130), ("RESULTADO", "Result", 0), ("SAÍDA", "ExitCode", 75), ("SEGUNDOS", "Seconds", 90)); stageGrid.MaxHeight = 280;
        sessionOutput = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 260, FontFamily = new FontFamily("Consolas"), FontSize = 12 };
        forceButton = Button("Confirmar parada forçada desta sessão", () => { session?.ConfirmForce(); UpdateSessionWidgets(); }); forceButton.IsEnabled = false;
        memoryActions = Actions(Button("Ver apps candidatos", () => _ = Execute(() => ReviewProfile(true))),
            Button("Tentar novamente a memória", () => _ = Execute(RecheckSessionMemory)), Button("Prosseguir mesmo assim", () => session?.AllowMemory(true)));
        memoryActions.Visibility = Visibility.Collapsed;
        artifactSelector = new ComboBox { MinWidth = 200, MinHeight = 34, Margin = new Thickness(0, 0, 10, 0) };
        artifactActions = Stack(Muted("Escolha o JAR produzido antes de instalar:"), artifactSelector, Button("Usar este JAR", () => { if (artifactSelector.SelectedItem is string jar) session?.ChooseArtifact(jar); else Status("Selecione um artefato da lista."); })); artifactActions.Visibility = Visibility.Collapsed;
        PageHost.Children.Add(Card(Stack(Text("Sessão e evidências", 20, true), sessionState, memoryActions, artifactActions, stageGrid,
            Actions(Button("Parar / cancelar normalmente", () => { session?.RequestStop(); UpdateSessionWidgets(); }), forceButton), sessionOutput)));
        BuildProfile(true);
    }
    private static string DefaultJava()
    {
        try { return SafePaths.Executable("java.exe", AppContext.BaseDirectory); } catch { return ""; }
    }
    private ProjectConfig ReadProject() => new()
    {
        Name = Value("name"), Folder = Value("folder"), Java = Value("java"), ServerFolder = Value("server"), ServerJar = Value("serverJar"), ServerArguments = JsonSerializer.Deserialize<List<string>>(Value("serverArgs")) ?? [],
        Artifact = Value("artifact"), PluginFileName = Value("pluginFile"),
        Build = new() { Executable = Value("buildExe"), Arguments = JsonSerializer.Deserialize<List<string>>(Value("buildArgs")) ?? [] },
        Tests = new() { Executable = Value("testExe"), Arguments = JsonSerializer.Deserialize<List<string>>(Value("testArgs")) ?? [] },
        XmsMb = Number("xms"), XmxMb = Number("xmx"), NativeMarginMb = Number("nativeMargin"), BuildAvailableMb = Number("buildMargin"), CommitMarginMb = Number("commitMargin"),
        Ports = Value("ports"), ReadyPattern = Value("ready"), PluginPattern = Value("pluginPattern"), TestPassPattern = Value("testsPattern"), ReadyTimeoutSeconds = Number("timeout")
    };
    private void DetectProject()
    {
        var detected = ProjectValidation.Detect(Value("folder"), Value("java"), Value("server"));
        fields["name"].Text = detected.Name; fields["buildExe"].Text = detected.Build.Executable; fields["buildArgs"].Text = JsonSerializer.Serialize(detected.Build.Arguments);
        fields["testExe"].Text = detected.Tests.Executable; fields["testArgs"].Text = JsonSerializer.Serialize(detected.Tests.Arguments);
        Status("Comandos detectados. Revise JDK, argumentos, artefato e compatibilidade antes de salvar.");
    }
    private void SaveProject()
    {
        var config = ReadProject(); ProjectValidation.Validate(config);
        var old = settings.Projects.FirstOrDefault(p => string.Equals(p.Folder, config.Folder, StringComparison.OrdinalIgnoreCase));
        if (old is not null) settings.Projects.Remove(old);
        settings.Projects.Add(config); editing = config; store.Save(settings); Status("Projeto e ambiente salvos localmente.");
    }
    private async Task CheckProject()
    {
        var config = ReadProject(); var check = await Task.Run(() => ProjectValidation.Check(config));
        if (preflightText is not null) preflightText.Text = string.Join("\n", check.Notes);
        if (proceedButton is not null) proceedButton.IsEnabled = check.EnvironmentValid && !check.MemoryEnough;
        Status(!check.EnvironmentValid ? "Corrija o ambiente antes de executar." : check.MemoryEnough ? "Ambiente válido e margem disponível no instante da checagem." : "Ambiente válido, mas a margem de memória é insuficiente. Revise candidatos ou prossiga explicitamente.");
    }
    private async Task StartProject(bool allowLow)
    {
        if (sessionTask?.IsCompleted == false || session?.HasProcesses == true) throw new InvalidOperationException("Pare a sessão atual antes de iniciar outra.");
        var config = ReadProject(); var check = await Task.Run(() => ProjectValidation.Check(config));
        if (preflightText is not null) preflightText.Text = string.Join("\n", check.Notes);
        if (!check.EnvironmentValid) { Status("Ambiente inválido; consulte a checagem."); return; }
        if (!check.MemoryEnough && !allowLow) { if (proceedButton is not null) proceedButton.IsEnabled = true; Status("Sem margem de memória. Veja candidatos, tente novamente ou prossiga mesmo assim."); return; }
        session?.Dispose(); session = new(store); logVersion = -1; stageCountShown = -1;
        session.Changed += () => { if (!closed) Dispatcher.BeginInvoke(UpdateSessionWidgets); };
        sessionTask = Task.Run(() => session.RunAsync(config, allowLow));
        _ = ObserveSession(sessionTask); UpdateSessionWidgets();
    }
    private async Task ObserveSession(Task task)
    {
        try { await task; } catch (Exception ex) { Status("A sessão não iniciou: " + ex.Message); }
        if (!closed) UpdateSessionWidgets();
    }
    private async Task RecheckSessionMemory()
    {
        if (session is null) return;
        var check = await Task.Run(session.RecheckMemory);
        if (preflightText is not null) preflightText.Text = string.Join("\n", check.Notes);
        if (!check.EnvironmentValid || !check.MemoryEnough) Status("Ainda falta margem ou o ambiente ficou inválido. Confira as leituras antes de prosseguir.");
    }
    private void UpdateSessionWidgets()
    {
        if (closed || session is null) return;
        if (sessionState is not null) sessionState.Text = session.State;
        if (stageGrid is not null && stageCountShown != session.Stages.Count)
        { stageGrid.ItemsSource = session.Stages.ToArray().GroupBy(s => s.Stage).Select(g => g.Last()).ToList(); stageCountShown = session.Stages.Count; }
        if (sessionOutput is not null && session.Log.Version != logVersion) { sessionOutput.Text = session.Log.Text; sessionOutput.ScrollToEnd(); logVersion = session.Log.Version; }
        if (forceButton is not null) forceButton.IsEnabled = session.StopAttempted && session.HasProcesses;
        if (runButton is not null) runButton.IsEnabled = sessionTask?.IsCompleted != false && !session.HasProcesses;
        if (memoryActions is not null) memoryActions.Visibility = session.NeedsMemoryDecision ? Visibility.Visible : Visibility.Collapsed;
        if (artifactActions is not null && artifactSelector is not null)
        {
            artifactActions.Visibility = session.ArtifactChoices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!ReferenceEquals(artifactSelector.ItemsSource, session.ArtifactChoices)) artifactSelector.ItemsSource = session.ArtifactChoices;
        }
        session.ReleaseLockIfStopped();
    }
    private void History()
    {
        var records = store.ReadList<SessionRecord>("sessions.json").AsEnumerable().Reverse().ToList();
        var table = Table(("QUANDO (UTC)", "At", 180), ("FLUXO", "Kind", 160), ("RESULTADO", "Result", 0)); table.MaxHeight = 360; table.ItemsSource = records;
        var evidence = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Height = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        table.SelectionChanged += (_, _) => evidence.Text = (table.SelectedItem as SessionRecord)?.Evidence ?? "";
        PageHost.Children.Add(Card(Stack(Text("Últimas 200 sessões e preparações", 20, true), table, Muted("Selecione um resultado para consultar as evidências."), evidence)));
        PageHost.Children.Add(Card(Stack(Text("Amostras locais", 18, true), Muted($"{telemetry.Samples.Count} amostras em memória. Retenção: {settings.RetentionHours} horas, até 20 mil amostras. Sem coleta enquanto o app estiver fechado."))));
    }
    private void SettingsPage()
    {
        var form = new StackPanel();
        Field(form, "sample", "Coleta com tela ativa (segundos)", settings.SampleSeconds.ToString());
        Field(form, "background", "Coleta em segundo plano (segundos)", settings.BackgroundSeconds.ToString());
        Field(form, "processInterval", "Intervalo mínimo da lista de processos (segundos)", settings.ProcessSeconds.ToString());
        Field(form, "retention", "Retenção do histórico (horas)", settings.RetentionHours.ToString());
        Field(form, "low", "Alerta: RAM disponível abaixo de (MB)", settings.LowAvailableMb.ToString());
        Field(form, "duration", "Por quantos segundos consecutivos", settings.LowDurationSeconds.ToString());
        Field(form, "commit", "Alerta: comprometimento a partir de (%)", settings.CommitPercent.ToString());
        Field(form, "pool", "Alerta: crescimento do pool não paginável (MB)", settings.PoolGrowthMb.ToString());
        Field(form, "poolWindow", "Janela de crescimento do pool (minutos)", settings.PoolWindowMinutes.ToString());
        form.Children.Add(Button("Salvar configurações", () =>
        {
            var clone = JsonSerializer.Deserialize<BoosterSettings>(JsonSerializer.Serialize(settings))!;
            clone.SampleSeconds = Number("sample"); clone.BackgroundSeconds = Number("background"); clone.ProcessSeconds = Number("processInterval"); clone.RetentionHours = Number("retention");
            clone.LowAvailableMb = Number("low"); clone.LowDurationSeconds = Number("duration"); clone.CommitPercent = Number("commit"); clone.PoolGrowthMb = Number("pool"); clone.PoolWindowMinutes = Number("poolWindow");
            clone.Validate(); store.Save(clone);
            settings.SampleSeconds = clone.SampleSeconds; settings.BackgroundSeconds = clone.BackgroundSeconds; settings.ProcessSeconds = clone.ProcessSeconds; settings.RetentionHours = clone.RetentionHours;
            settings.LowAvailableMb = clone.LowAvailableMb; settings.LowDurationSeconds = clone.LowDurationSeconds; settings.CommitPercent = clone.CommitPercent; settings.PoolGrowthMb = clone.PoolGrowthMb; settings.PoolWindowMinutes = clone.PoolWindowMinutes;
            timer.Interval = TimeSpan.FromSeconds(settings.SampleSeconds); Status("Configurações salvas. O app mantém permissões normais e não altera serviços, paginação, registro, segurança ou energia.");
        }, true));
        PageHost.Children.Add(Card(form));
        if (team.User is not null) PageHost.Children.Add(AccountCard());
        var advanced = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (int target in new[] { PageMonitor, PageDiagnosis, PageGames, PageWork, PageDev, PageHistory })
        { int t = target; advanced.Children.Add(Button(icons[t] + "  " + titles[t], () => Navigate(t))); }
        PageHost.Children.Add(Card(Stack(Mono("AVANÇADO", 11, "#EDEDED"), Muted("Telas detalhadas para quem quer controlar cada passo. O botão Boost já faz tudo isso automaticamente."), advanced)));
        PageHost.Children.Add(Card(Stack(Text("Dados ficam neste PC", 18, true), Muted(store.Root), Muted("Logs: até 1 MB por segmento e até 40 arquivos recentes. Histórico limitado. Não há limpeza de cache nem ajustes automáticos no Windows."))));
    }
    private void ApplyLayout()
    {
        // A barra de canais some em janelas estreitas ou pelo botão ☰; o trilho de servidores fica sempre.
        bool collapse = sidebarCollapsed;
        SidebarColumn.Width = new GridLength(collapse ? 0 : 240);
        if (metricsGrid is not null) metricsGrid.Columns = ActualWidth < 1120 ? 1 : 3;
    }
    private void LayoutChanged(object sender, SizeChangedEventArgs e) { if (Rail is not null) ApplyLayout(); }
    private void SettingsClicked(object sender, RoutedEventArgs e) => Navigate(PageSettings);
    private void ToggleSidebar(object sender, RoutedEventArgs e) { sidebarCollapsed = !sidebarCollapsed; ApplyLayout(); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (sessionTask?.IsCompleted == false || session?.HasProcesses == true || game?.HasActiveProcesses == true)
        { e.Cancel = true; Status("Há uma sessão iniciada pelo app em andamento. Solicite a parada em Desenvolvimento/Jogos. Se não terminar, confirme separadamente a parada forçada; depois feche a janela."); }
    }
    internal async Task RunUiTest()
    {
        for (int i = 0; i < 200 && (latest is null || sampling); i++) await Task.Delay(100);
        timer.Stop(); logTimer.Stop();
        if (latest is null || Rail.Children.Count < 2 || Application.Current.Windows.Count != 1) throw new Exception("Dashboard/session integration failed.");
        ShowLogin(); await CapturePreview("login-preview.png");
        SwitchMode(true); await CapturePreview("register-preview.png"); SwitchMode(false);
        HideLogin();
        Navigate(PageGroups); if (FullHost.Visibility != Visibility.Visible) throw new Exception("Groups page must use the full-height host."); await CapturePreview("groups-offline-preview.png");
        Navigate(PageOverview); for (int i = 0; i < 150 && !gamesScanned; i++) await Task.Delay(100); await Task.Delay(1500); await CapturePreview("ui-preview.png"); if (boostButton is null) throw new Exception("Boost button missing."); teamTestNote = $" Games detected: {games.Count} ({string.Join(", ", games.Take(12).Select(g => g.Source + ":" + g.Name))})." + teamTestNote;
        Navigate(PageMonitor); await CapturePreview("monitor-preview.png");
        var native = latest; latest = latest with { Committed = null, CommitLimit = null, PagedPool = null, NonpagedPool = null, Cache = null };
        UpdateDashboard(); if (commitMetric?.Text != "Indisponível") throw new Exception("Unavailable metrics were treated as zero."); latest = native; UpdateDashboard();
        Width = MinWidth; Height = MinHeight; await CapturePreview("ui-compact-preview.png"); Width = 1360; Height = 940;
        Navigate(PageDiagnosis); if (processGrid is null || alerts is null) throw new Exception("Diagnosis controls unavailable."); await CapturePreview("diagnosis-preview.png");
        Navigate(PageGames); if (ruleRows.Any(r => r.Action != ProfileAction.Preservar)) throw new Exception("Default profile must preserve everything."); await CapturePreview("games-preview.png");
        Navigate(PageWork); if (ruleRows.Any(r => r.Action != ProfileAction.Preservar)) throw new Exception("Work profile must preserve everything by default."); await CapturePreview("work-preview.png");
        Navigate(PageDev); if (!fields.ContainsKey("serverJar") || stageGrid is null || sessionOutput is null) throw new Exception("Minecraft integration missing."); await CapturePreview("development-preview.png");
        Navigate(PageHistory); Navigate(PageSettings); if (!fields.ContainsKey("commit")) throw new Exception("Configurable rules missing.");
        if (Application.Current.Windows.Count != 1) throw new Exception("Unexpected extra window.");
        if (Environment.GetEnvironmentVariable("PCBOOST_TEAM_TEST") is string server && server.Length > 0) await RunTeamUiTest(server);
        File.WriteAllText("ui-test-result.txt", $"PASS: dark dashboard ({FileCleaner.FormatSize(latest.Available)} available), login overlay, eight pages, groups host, compact sidebar, preserve-by-default profiles, project editor, session log, history, settings." + (teamTestNote ?? " Team server UI not exercised (set PCBOOST_TEAM_TEST).") + " No external apps closed/launched and no real Minecraft server executed by UI test.");
    }
    internal async Task MeasureIdle()
    {
        for (int i = 0; i < 200 && (latest is null || sampling); i++) await Task.Delay(100);
        Navigate(0); measureForeground = true; timer.Interval = TimeSpan.FromSeconds(settings.SampleSeconds);
        await Task.Delay(5000);
        using var self = Process.GetCurrentProcess(); var before = self.TotalProcessorTime; var clock = Stopwatch.StartNew();
        await Task.Delay(30000); self.Refresh();
        double corePercent = (self.TotalProcessorTime - before).TotalMilliseconds / clock.Elapsed.TotalMilliseconds * 100;
        File.WriteAllText("idle-test-result.txt", $"Windows idle measurement, dashboard open, metrics every {settings.SampleSeconds}s / process list every {settings.ProcessSeconds}s. Window: {clock.Elapsed.TotalSeconds:N1}s. CPU: {corePercent:N2}% of one core / {corePercent / Environment.ProcessorCount:N3}% of total CPU. Working set: {self.WorkingSet64 / 1048576d:N1} MB. Private commit: {self.PrivateMemorySize64 / 1048576d:N1} MB. Samples retained: {telemetry.Samples.Count}. No external apps modified.");
    }
    private async Task CapturePreview(string name)
    {
        UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); UpdateLayout(); DrawChart();
        UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render); UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(name); encoder.Save(file);
    }
}
