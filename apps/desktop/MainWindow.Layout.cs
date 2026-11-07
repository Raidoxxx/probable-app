using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PCBoost.Shared;

namespace PCBoost;

// Layout flutuante: a área principal ocupa a janela e quatro painéis ficam por cima dela —
// topo (busca e ações), canais (esquerda), membros/voz/booster (direita) e servidores (embaixo).
// Cada painel recua sozinho para a borda quando o mouse sai dele; encostar o mouse na borda traz de volta.
// O alfinete do topo fixa todos e reserva espaço para eles em volta do conteúdo.
public partial class MainWindow
{
    private enum Edge { Top, Left, Right, Bottom }
    private sealed class Drawer(FrameworkElement panel, FrameworkElement handle, Edge edge)
    {
        public FrameworkElement Panel { get; } = panel;
        public FrameworkElement Handle { get; } = handle;
        public Edge Edge { get; } = edge;
        public TranslateTransform Shift { get; } = new();
        public bool Open { get; set; } = true;
        public DateTime HoldUntil { get; set; }
    }
    private readonly List<Drawer> drawers = [];
    private readonly DispatcherTimer drawerTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private static readonly TimeSpan DrawerDelay = TimeSpan.FromMilliseconds(650);
    private const double EdgeZone = 14, Slack = 28, Shadow = 30;
    private readonly HashSet<string> foldedSections = [];
    private TextBlock? boosterAction, boosterState;
    private Action? searchFirst;

    private void InitDrawers()
    {
        drawers.AddRange([new(TopBar, TopHandle, Edge.Top), new(LeftPanel, LeftHandle, Edge.Left), new(RightPanel, RightHandle, Edge.Right), new(BottomDock, BottomHandle, Edge.Bottom)]);
        foreach (var d in drawers) d.Panel.RenderTransform = d.Shift;
        drawerTimer.Tick += (_, _) => UpdateDrawers();
        PreviewKeyDown += ShortcutPressed;
        RailScroll.PreviewMouseWheel += (_, e) => { RailScroll.ScrollToHorizontalOffset(RailScroll.HorizontalOffset - e.Delta / 2.0); e.Handled = true; };
        Closed += (_, _) => drawerTimer.Stop();
        ApplyPinGlyph();
    }
    private bool Pinned => settings.PinPanels || uiTestMode;
    private Drawer DrawerAt(Edge edge) => drawers.First(d => d.Edge == edge);

    // Ao abrir o app os painéis aparecem por um instante, para mostrar onde cada coisa fica, e então recuam.
    private void RevealDrawers(double seconds = 1.6) { foreach (var d in drawers) ShowDrawer(d.Edge, seconds); }
    private void ShowDrawer(Edge edge, double seconds = 1.2)
    {
        if (drawers.Count == 0) return;
        var d = DrawerAt(edge); d.HoldUntil = DateTime.UtcNow.AddSeconds(seconds); Slide(d, true); drawerTimer.Start();
    }
    private void ShellMouseMove(object sender, MouseEventArgs e) => UpdateDrawers();
    private void UpdateDrawers()
    {
        if (closed || drawers.Count == 0 || !IsLoaded) return;
        var now = DateTime.UtcNow; var pointer = Mouse.GetPosition(Shell);
        bool inside = LoginOverlay.Visibility != Visibility.Visible && pointer.X >= 0 && pointer.Y >= 0 && pointer.X <= Shell.ActualWidth && pointer.Y <= Shell.ActualHeight;
        bool pending = false;
        foreach (var d in drawers)
        {
            if (Wanted(d, pointer, inside)) { if (d.HoldUntil < now + DrawerDelay) d.HoldUntil = now + DrawerDelay; Slide(d, true); }
            else if (d.Open && now >= d.HoldUntil) Slide(d, false);
            pending |= d.Open && !Pinned;
        }
        if (!pending) drawerTimer.Stop(); else if (!drawerTimer.IsEnabled) drawerTimer.Start();
    }
    private bool Wanted(Drawer d, Point p, bool inside)
    {
        if (Pinned || d.Panel.IsMouseOver) return true;
        // Digitando num campo do painel (busca, nome de canal novo) ou com um menu dele aberto: fica.
        if (Keyboard.FocusedElement is TextBoxBase focused && IsInside(focused, d.Panel)) return true;
        if (d.Edge == Edge.Left && ServerMenu.IsOpen || d.Edge == Edge.Top && SearchPopup.IsOpen) return true;
        if (!inside) return false;
        double width = Shell.ActualWidth, height = Shell.ActualHeight;
        var box = d.Panel.TransformToAncestor(Shell).TransformBounds(new Rect(d.Panel.RenderSize));
        bool alongX = p.X >= box.Left - Slack && p.X <= box.Right + Slack, alongY = p.Y >= box.Top - Slack && p.Y <= box.Bottom + Slack;
        bool atEdge = d.Edge switch
        {
            Edge.Top => p.Y <= EdgeZone && alongX,
            Edge.Bottom => p.Y >= height - EdgeZone && alongX,
            Edge.Left => p.X <= EdgeZone && alongY,
            _ => p.X >= width - EdgeZone && alongY,
        };
        if (atEdge || !d.Open) return atEdge;
        // Já aberto: o vão até a borda e uma folga em volta também contam, para o painel não piscar.
        return d.Edge switch
        {
            Edge.Top => alongX && p.Y <= box.Bottom + Slack,
            Edge.Bottom => alongX && p.Y >= box.Top - Slack,
            Edge.Left => alongY && p.X <= box.Right + Slack,
            _ => alongY && p.X >= box.Left - Slack,
        };
    }
    private void Slide(Drawer d, bool open, bool instant = false)
    {
        if (d.Open == open && !instant) return;
        d.Open = open;
        var margin = d.Panel.Margin;
        double to = open ? 0 : d.Edge switch
        {
            Edge.Top => -(d.Panel.ActualHeight + margin.Top + Shadow),
            Edge.Bottom => d.Panel.ActualHeight + margin.Bottom + Shadow,
            Edge.Left => -(d.Panel.ActualWidth + margin.Left + Shadow),
            _ => d.Panel.ActualWidth + margin.Right + Shadow,
        };
        var property = d.Edge is Edge.Top or Edge.Bottom ? TranslateTransform.YProperty : TranslateTransform.XProperty;
        bool still = instant || uiTestMode || !SystemParameters.ClientAreaAnimation;
        if (still)
        {
            d.Shift.BeginAnimation(property, null); d.Shift.SetValue(property, to);
            d.Handle.BeginAnimation(OpacityProperty, null); d.Handle.Opacity = open ? 0 : 1;
            return;
        }
        d.Shift.BeginAnimation(property, new DoubleAnimation(to, TimeSpan.FromMilliseconds(open ? 170 : 260)) { EasingFunction = new CubicEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn } });
        d.Handle.BeginAnimation(OpacityProperty, new DoubleAnimation(open ? 0 : 1, TimeSpan.FromMilliseconds(200)));
    }
    private void ApplyDrawerLayout()
    {
        if (drawers.Count == 0) return;
        double width = ActualWidth;
        if (width > 0) { TopBar.Width = Math.Clamp(width - 48, 320, 680); BottomDock.MaxWidth = Math.Max(280, width - 48); }
        // Fixados, os painéis viram moldura: o conteúdo fica no meio, sem nada por cima.
        Main.Margin = Pinned ? new Thickness(LeftPanel.Width + LeftPanel.Margin.Left + 12, LeftPanel.Margin.Top, RightPanel.Width + RightPanel.Margin.Right + 12, LeftPanel.Margin.Bottom) : new Thickness(0);
        foreach (var d in drawers) if (!d.Open) Slide(d, false, instant: true);
        UpdateDrawers();
    }
    private void PinClicked(object sender, RoutedEventArgs e)
    {
        settings.PinPanels = !settings.PinPanels;
        try { store.Save(settings); } catch (Exception ex) { Status(ex.Message); }
        ApplyPinGlyph(); ApplyLayout();
        Status(settings.PinPanels ? "Painéis fixos. Clique no alfinete de novo para eles recuarem sozinhos." : "Painéis recuam sozinhos. Encoste o mouse na borda para trazer de volta.");
    }
    private void ApplyPinGlyph()
    {
        PinGlyph.Text = settings.PinPanels ? "\uE840" : "\uE718";
        PinGlyph.Foreground = settings.PinPanels ? (Brush)FindResource("Ink") : Brush("#CFCFCF");
        PinButton.Background = settings.PinPanels ? Brush("#262626") : Brushes.Transparent;
        PinButton.ToolTip = settings.PinPanels ? "Painéis fixos · clique para eles recuarem sozinhos" : "Fixar painéis na tela";
    }
    private void ShortcutPressed(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.K || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || LoginOverlay.Visibility == Visibility.Visible) return;
        e.Handled = true; ShowDrawer(Edge.Top); SearchBox.Focus(); SearchBox.SelectAll();
    }

    // ===================== Painel da direita: membros, voz e booster =====================
    private void RenderPeople()
    {
        if (PeopleHost is null) return;
        foreach (var list in talkRings.Values) list.RemoveAll(r => IsInside(r, PeopleHost));
        PeopleHost.Children.Clear(); boosterAction = boosterState = null;
        if (InServer)
        {
            bool owner = current!.OwnerId == team.User?.Id;
            if (Section("membros", "\uE716", $"Membros ({members.Count})", null))
                foreach (var m in members.OrderByDescending(m => m.Online).ThenBy(m => m.Name)) PeopleHost.Children.Add(MemberRow(m));
            if (Section("voz", "\uE720", "Voz", owner ? () => { creatingKind = ChannelKind.Voice; RenderSidebar(); } : null))
            {
                var voices = channels.Where(c => c.Kind == ChannelKind.Voice).ToList();
                foreach (var c in voices) PeopleHost.Children.Add(ChannelRow(c, owner));
                if (creatingKind == ChannelKind.Voice) PeopleHost.Children.Add(NewChannelBox(ChannelKind.Voice));
                else if (voices.Count == 0) PeopleHost.Children.Add(Note("Nenhum canal de voz."));
            }
        }
        else PeopleHost.Children.Add(Note(team.User is null ? "Entre numa conta para ver membros e canais de voz." : "Abra um servidor no dock de baixo para ver membros e canais de voz."));
        if (Section("booster", "♛", "Booster", null))
        {
            PeopleHost.Children.Add(PanelRow("\uE945", "", () => Try(ToggleBoost), out var action)); boosterAction = action;
            PeopleHost.Children.Add(PanelRow("\uE7FC", "Jogos do PC", () => Navigate(PageOverview), out _));
            PeopleHost.Children.Add(PanelRow("\uE9D9", "Monitor de memória", () => Navigate(PageMonitor), out _));
            boosterState = Mono("", 9.5, "#5C5C5C"); boosterState.Margin = new Thickness(10, 6, 8, 0); PeopleHost.Children.Add(boosterState);
            UpdateBoosterPanel();
        }
    }
    private void UpdateBoosterPanel()
    {
        bool on = autoBoost?.Active == true;
        if (boosterAction is not null) boosterAction.Text = on ? "Desligar boost" : "Dar boost agora";
        if (boosterState is null) return;
        boosterState.Text = boosting ? "LIBERANDO O PC…" : on ? "● ATIVO · " + autoBoost!.Summary : "BOOST DESLIGADO";
        boosterState.Foreground = on ? (Brush)FindResource("Accent") : Brush("#5C5C5C");
    }
    // Cabeçalho de seção dobrável (chevron + ícone + título), como no rascunho.
    private bool Section(string key, string glyph, string title, Action? add)
    {
        bool open = !foldedSections.Contains(key);
        if (PeopleHost.Children.Count > 0) PeopleHost.Children.Add(new Border { Height = 1, Background = Brush("#222222"), Margin = new Thickness(4, 10, 4, 6) });
        var head = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(4, 2, 2, 4) };
        if (add is not null)
        {
            var plus = new Button { Content = Mono("+", 13, "#8C8C8C"), Style = (Style)FindResource("GhostButton"), Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(0), ToolTip = "Criar canal de voz" };
            plus.Click += (_, _) => add(); DockPanel.SetDock(plus, Dock.Right); head.Children.Add(plus);
        }
        head.Children.Add(GlyphText(open ? "\uE70D" : "\uE76C", 9, "#8C8C8C", 18));
        head.Children.Add(GlyphText(glyph, 14, "#CFCFCF", 26));
        head.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        head.MouseLeftButtonUp += (_, _) => { if (!foldedSections.Remove(key)) foldedSections.Add(key); RenderPeople(); };
        PeopleHost.Children.Add(head);
        return open;
    }
    private TextBlock GlyphText(string glyph, double size, string color, double width) => new()
    {
        Text = glyph, FontSize = size, Foreground = Brush(color), Width = width, VerticalAlignment = VerticalAlignment.Center,
        FontFamily = glyph[0] >= '\uE000' ? (FontFamily)FindResource("Icons") : new FontFamily("Segoe UI Symbol")
    };
    private Border PanelRow(string glyph, string label, Action click, out TextBlock text)
    {
        var row = new DockPanel();
        row.Children.Add(GlyphText(glyph, 13, "#8C8C8C", 26));
        text = new TextBlock { Text = label, FontSize = 13, Foreground = Brush("#CFCFCF"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        row.Children.Add(text);
        var item = new Border { Child = row, Padding = new Thickness(8, 7, 8, 7), Margin = new Thickness(18, 1, 0, 1), CornerRadius = new CornerRadius(4), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        item.MouseEnter += (_, _) => item.Background = Brush("#171717"); item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
        item.MouseLeftButtonUp += (_, _) => click();
        return item;
    }
    private static TextBlock Note(string text) { var note = Muted(text, 12); note.Margin = new Thickness(10, 4, 8, 6); return note; }

    // ===================== Topo: sino, busca =====================
    private void UpdateNotifyBadge()
    {
        int total = unread.Values.Sum(u => u.Count);
        NotifyBadge.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotifyCount.Text = total > 99 ? "99+" : total.ToString(CultureInfo.InvariantCulture);
        NotifyButton.ToolTip = total == 0 ? "Sem mensagens novas" : total == 1 ? "1 mensagem nova · clique para abrir" : $"{total} mensagens novas · clique para abrir";
    }
    // O sino leva direto ao canal com mensagens novas.
    private void NotifyClicked(object sender, RoutedEventArgs e)
    {
        var next = unread.Where(u => u.Value.Count > 0).Select(u => (Channel: u.Key, u.Value.Group)).FirstOrDefault();
        if (next.Channel == 0) { Status("Nenhuma mensagem nova."); return; }
        var group = groups.FirstOrDefault(g => g.Id == next.Group);
        if (group is null) { unread.Remove(next.Channel); RenderRail(); return; }
        Try(async () =>
        {
            if (!InServer || current!.Id != group.Id) { lastChannel[group.Id] = next.Channel; await SelectGroup(group); return; }
            if (channels.FirstOrDefault(c => c.Id == next.Channel) is { } target) await SelectChannel(target);
        });
    }
    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        string query = SearchBox.Text.Trim();
        SearchHint.Visibility = SearchKeyHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchList.Children.Clear(); searchFirst = null;
        if (query.Length == 0) { SearchPopup.IsOpen = false; return; }
        foreach (var (glyph, label, kind, go) in SearchResults(query).Take(8))
        {
            void Run() { SearchBox.Clear(); SearchPopup.IsOpen = false; Keyboard.ClearFocus(); go(); }
            searchFirst ??= Run;
            var row = new DockPanel();
            var tag = Mono(kind, 9.5, "#5C5C5C"); tag.TextWrapping = TextWrapping.NoWrap; tag.Margin = new Thickness(12, 0, 0, 0); tag.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(tag, Dock.Right); row.Children.Add(tag);
            row.Children.Add(new TextBlock { Text = glyph, Width = 24, Foreground = Brush("#8C8C8C"), VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Segoe UI, Segoe UI Symbol, Segoe UI Emoji") });
            row.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = Brush("#EDEDED"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var button = new Button { Content = row, Style = (Style)FindResource("GhostButton"), HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0), Focusable = false };
            if (SearchList.Children.Count == 0) button.Background = Brush("#1A1A1A"); // Enter abre o primeiro
            button.Click += (_, _) => Run();
            SearchList.Children.Add(button);
        }
        if (SearchList.Children.Count == 0) { var none = Mono("NADA ENCONTRADO", 10, "#5C5C5C"); none.Margin = new Thickness(10, 8, 10, 8); SearchList.Children.Add(none); }
        SearchPopup.IsOpen = true;
    }
    private IEnumerable<(string Glyph, string Label, string Kind, Action Go)> SearchResults(string query)
    {
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        bool Match(string text) => compare.IndexOf(text, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
        if (InServer)
            foreach (var c in channels.Where(c => Match(c.Name)))
                yield return (Glyph(c.Kind), c.Name, c.Kind == ChannelKind.Voice ? "VOZ" : c.Kind == ChannelKind.Board ? "QUADRO" : "CANAL", () => Try(() => SelectChannel(c, join: false)));
        foreach (var g in groups.Where(g => Match(g.Name))) yield return ("◎", g.Name, "SERVIDOR", () => Try(() => SelectGroup(g)));
        for (int i = 0; i < titles.Length; i++)
        {
            int target = i;
            if (Match(titles[i])) yield return (icons[i], titles[i], "TELA", () => { if (target == PageGroups && team.User is null) ShowLogin(); else Navigate(target); });
        }
        foreach (var game in games.Where(g => Match(g.Name))) yield return ("▶", game.Name, "BOOST + JOGAR", () => Try(() => BoostGame(game)));
    }
    private void SearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && searchFirst is not null) { e.Handled = true; searchFirst(); }
        else if (e.Key == Key.Escape) { e.Handled = true; SearchBox.Clear(); SearchPopup.IsOpen = false; Keyboard.ClearFocus(); }
    }
}
