using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PCBoost.Shared;
using PCBoost.Team;

namespace PCBoost;

// Conta, entrada e a área de servidores no formato do Discord:
// trilho de servidores · barra de canais (texto, quadro, voz) · conteúdo · membros · painel do usuário.
public partial class MainWindow
{
    private readonly TeamClient team;
    private readonly DispatcherTimer voiceTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private List<GroupDto> groups = [];
    private GroupDto? current;
    private List<ChannelDto> channels = [];
    private ChannelDto? channel;
    private readonly Dictionary<long, long> lastChannel = [];
    private List<MessageDto> chat = [];
    private List<MemberDto> members = [];
    private List<IdeaDto> ideas = [];
    private readonly Dictionary<long, List<CallParticipant>> voiceStates = [];       // canal de voz → participantes
    private readonly Dictionary<long, (long Group, int Count)> unread = [];           // canal de texto → não lidas
    private VoiceCall? voice;
    private ChannelDto? callChannel;
    private GroupDto? callGroup;
    private bool micMuted, deafened, chatHasMore, registerMode, addServer, membersVisible = true;
    private string? creatingKind;
    private IdeaDto? editingIdea;
    private string? newIdeaStatus;
    private string? teamTestNote;
    private string connectionLabel = "DESCONECTADO";
    private BitmapCacheKey loginArtKey;

    // Referências do conteúdo (recriadas a cada navegação).
    private StackPanel? chatPanel, membersPanel;
    private ScrollViewer? chatScroll;
    private Grid? boardHost, voiceHost;
    private TextBox? chatInput;
    private readonly Dictionary<long, List<Shape>> talkRings = [];

    private readonly record struct BitmapCacheKey(int Width, int Height);

    private void InitTeam()
    {
        EmailBox.TextChanged += (_, _) => EmailHint.Visibility = EmailBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PasswordBox.PasswordChanged += (_, _) => PasswordHint.Visibility = PasswordBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var saved = team.LoadSaved(); ServerBox.Text = saved.Server; EmailBox.Text = saved.Email;
        if (TeamClient.HasOfficialServer) ServerExpander.Visibility = Visibility.Collapsed; // endereço já vem no app
        UpdateAccount();
        voiceTimer.Tick += (_, _) => UpdateTalking();
        team.Message += m => Ui(() => OnMessage(m));
        team.Presence += (g, u, on) => Ui(() =>
        {
            if (current?.Id != g) return;
            members = members.Select(x => x.UserId == u ? x with { Online = on } : x).ToList(); RenderMembers();
        });
        team.MembersChanged += g => Ui(async () =>
        {
            groups = await team.Groups(); RenderRail();
            if (current?.Id == g) { members = await team.Members(g); RenderMembers(); }
        });
        team.ChannelsChanged += g => Ui(async () =>
        {
            if (current?.Id != g) return;
            channels = await team.Channels(g);
            if (callChannel is not null && callChannel.GroupId == g && channels.All(c => c.Id != callChannel.Id)) await LeaveCall();
            if (channel is not null && channels.All(c => c.Id != channel.Id)) { await SelectChannel(channels.First(c => c.Kind == ChannelKind.Text)); return; }
            RenderSidebar();
        });
        team.IdeaChanged += (g, c, id) => Ui(async () =>
        {
            if (channel?.Id != c) return;
            var idea = await team.Idea(id); if (idea is null || channel?.Id != c) return;
            int index = ideas.FindIndex(i => i.Id == id);
            if (index >= 0) ideas[index] = idea; else ideas.Insert(0, idea);
            if (editingIdea?.Id == id) editingIdea = idea;
            RenderBoard(keepEditor: true);
        });
        team.IdeaDeleted += (g, c, id) => Ui(() =>
        {
            if (channel?.Id != c) return;
            ideas.RemoveAll(i => i.Id == id); if (editingIdea?.Id == id) editingIdea = null;
            RenderBoard(keepEditor: editingIdea is not null || newIdeaStatus is not null);
        });
        team.CallChanged += (g, c, list) => Ui(() =>
        {
            voiceStates[c] = list;
            if (callChannel?.Id == c && voice is not null)
                foreach (var gone in talkRings.Keys.Where(u => list.All(p => p.UserId != u)).ToList()) voice.Forget(gone);
            if (current?.Id == g && page == PageGroups) { RenderSidebar(); if (channel?.Id == c) RenderVoice(); }
        });
        team.Audio += (u, frame) => voice?.Receive(u, frame);
        team.ConnectionChanged += state => Ui(async () =>
        {
            bool reconnected = state == "CONECTADO" && connectionLabel == "RECONECTANDO";
            connectionLabel = state; UpdateAccount();
            if (!reconnected) return;
            if (callChannel is not null) { try { voiceStates[callChannel.Id] = await team.JoinCall(callChannel.Id, micMuted || deafened); } catch (Exception) { } }
            await LoadGroups(current?.Id);
        });
    }
    private void Ui(Action action) { if (!closed) Dispatcher.BeginInvoke(() => { try { action(); } catch (Exception ex) { Status(ex.Message); } }); }
    private void Ui(Func<Task> action) { if (!closed) Dispatcher.BeginInvoke(async () => { try { await action(); } catch (Exception ex) { Status(ex.Message); } }); }
    private async void Try(Func<Task> action) { try { await action(); } catch (Exception ex) { Status(ex.Message); } }

    // ===================== Entrada =====================
    private async Task ResumeLogin()
    {
        if (team.LoadSaved().ProtectedToken.Length == 0) { ShowLogin(); return; }
        LoginButton.IsEnabled = false; LoginButton.Content = "Conectando…";
        bool ok = await team.Resume();
        LoginButton.IsEnabled = true; LoginButton.Content = registerMode ? "Criar conta" : "Entrar";
        if (ok) { HideLogin(); UpdateAccount(); await LoadGroups(null, navigate: false); }
        else ShowLogin();
    }
    private void ShowLogin()
    {
        LoginError.Visibility = Visibility.Collapsed; PasswordBox.Clear();
        LoginOverlay.Visibility = Visibility.Visible; RenderLoginArt();
        Dispatcher.BeginInvoke(() => (EmailBox.Text.Length == 0 ? (Control)EmailBox : PasswordBox).Focus(), DispatcherPriority.Input);
    }
    private void HideLogin() { LoginOverlay.Visibility = Visibility.Collapsed; PasswordBox.Clear(); }
    private void SwitchMode(bool register)
    {
        registerMode = register; NameRow.Visibility = register ? Visibility.Visible : Visibility.Collapsed;
        LoginButton.Content = register ? "Criar conta" : "Entrar";
        SwitchHint.Text = register ? "Já tem uma conta? " : "Não tem uma conta? "; SwitchLabel.Text = register ? "Entrar" : "Criar conta";
        PasswordHint.Text = register ? "Mínimo de 8 caracteres" : "Sua senha"; LoginError.Visibility = Visibility.Collapsed;
    }
    private void SwitchModeClicked(object sender, RoutedEventArgs e) => SwitchMode(!registerMode);
    private void LoginArtSized(object sender, SizeChangedEventArgs e) => RenderLoginArt();
    private void RenderLoginArt()
    {
        if (LoginOverlay.Visibility != Visibility.Visible || LoginArtHost.ActualWidth < 20 || LoginArtHost.ActualHeight < 20) return;
        var key = new BitmapCacheKey((int)LoginArtHost.ActualWidth, (int)LoginArtHost.ActualHeight);
        if (key == loginArtKey && LoginArt.Source is not null) return;
        loginArtKey = key; LoginArt.Source = DotArt.Columns(key.Width, key.Height);
    }
    private async void LoginClicked(object sender, RoutedEventArgs e)
    {
        void Fail(string message) { LoginError.Text = message; LoginError.Visibility = Visibility.Visible; }
        LoginError.Visibility = Visibility.Collapsed;
        if (EmailBox.Text.Trim().Length == 0 || PasswordBox.Password.Length == 0) { Fail("Informe e-mail e senha."); return; }
        if (registerMode && NameBox.Text.Trim().Length == 0) { Fail("Informe seu nome."); return; }
        if (registerMode && PasswordBox.Password.Length < 8) { Fail("A senha precisa ter pelo menos 8 caracteres."); return; }
        LoginButton.IsEnabled = false; LoginButton.Content = registerMode ? "Criando…" : "Entrando…";
        try
        {
            await team.SignIn(ServerBox.Text, EmailBox.Text, PasswordBox.Password, NameBox.Text, registerMode, RememberBox.IsChecked == true);
            HideLogin(); UpdateAccount();
            await LoadGroups(null);
        }
        catch (Exception ex) { Fail(ex.Message); }
        finally { LoginButton.IsEnabled = true; LoginButton.Content = registerMode ? "Criar conta" : "Entrar"; }
    }
    private void OfflineClicked(object sender, RoutedEventArgs e) { HideLogin(); UpdateAccount(); Status("Modo sem conta: o Boost funciona completo. Servidores, chat e chamadas pedem uma conta."); }
    private void AccountClicked(object sender, RoutedEventArgs e) { if (team.User is null) ShowLogin(); else Try(SignOut); }
    private async Task SignOut()
    {
        await LeaveCall();
        await team.SignOut(); groups = []; current = null; channel = null; channels = []; chat = []; ideas = []; members = []; voiceStates.Clear(); unread.Clear();
        UpdateAccount(); Navigate(PageOverview);
        ShowLogin(); Status("Você saiu da conta.");
    }
    private void UpdateAccount()
    {
        UserAvatarHost.Children.Clear();
        UserAvatarHost.Children.Add(team.User is null ? Avatar("?", 0, 32, null) : Avatar(team.User.Name, team.User.Id, 32, connectionLabel == "CONECTADO"));
        bool online = team.User is not null;
        AccountName.Text = team.User?.Name ?? "Sem conta";
        AccountState.Text = !online ? "OFFLINE · SÓ BOOST" : connectionLabel == "CONECTADO" ? "ONLINE" : connectionLabel;
        AccountState.Foreground = !online ? Brush("#5C5C5C") : connectionLabel == "CONECTADO" ? (Brush)FindResource("Accent") : (Brush)FindResource("Warn");
        AccountButton.Visibility = online ? Visibility.Collapsed : Visibility.Visible;
        MicButton.Visibility = DeafButton.Visibility = online ? Visibility.Visible : Visibility.Collapsed;
        MicGlyph.Foreground = micMuted || deafened ? (Brush)FindResource("Danger") : Brush("#CFCFCF");
        DeafGlyph.Foreground = deafened ? (Brush)FindResource("Danger") : Brush("#CFCFCF");
        MicButton.ToolTip = micMuted ? "Ativar microfone" : "Silenciar microfone"; DeafButton.ToolTip = deafened ? "Ativar áudio" : "Desativar áudio";
        VoicePanel.Visibility = callChannel is null ? Visibility.Collapsed : Visibility.Visible;
        CallBadge.Text = callChannel is null ? "" : $"{callChannel.Name} / {callGroup?.Name}";
    }
    private void MicClicked(object sender, RoutedEventArgs e) { micMuted = !micMuted; if (!micMuted && deafened) deafened = false; Try(ApplyVoiceState); }
    private void DeafClicked(object sender, RoutedEventArgs e) { deafened = !deafened; Try(ApplyVoiceState); }
    private void HangUpClicked(object sender, RoutedEventArgs e) => Try(LeaveCall);
    private async Task ApplyVoiceState()
    {
        if (voice is not null) { voice.Muted = micMuted || deafened; voice.Deafened = deafened; }
        UpdateAccount();
        if (callChannel is not null) await team.SetMuted(micMuted || deafened);
        if (page == PageGroups && channel?.Kind == ChannelKind.Voice) RenderVoice();
    }

    // ===================== Peças visuais =====================
    private static readonly string[] AvatarTones = ["#2B2B2B", "#353535", "#3F3F3F", "#262626", "#303030", "#454545"];
    private static string InitialsOf(string name) { var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries); return parts.Length == 0 ? "?" : string.Concat(parts.Take(2).Select(p => char.ToUpperInvariant(p[0]))); }
    // Avatar redondo com iniciais; ponto verde/cinza de presença como no Discord; anel opcional para quem está falando.
    private Grid Avatar(string name, long id, double size, bool? online, long talkUser = 0)
    {
        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(new Ellipse { Fill = Brush(AvatarTones[(int)(Math.Abs(id) % AvatarTones.Length)]) });
        grid.Children.Add(new TextBlock { Text = InitialsOf(name), FontSize = size * 0.36, FontWeight = FontWeights.SemiBold, Foreground = Brush("#EDEDED"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        if (talkUser != 0)
        {
            var ring = new Ellipse { Stroke = (Brush)FindResource("Accent"), StrokeThickness = Math.Max(2, size / 16), Margin = new Thickness(-3), Visibility = Visibility.Hidden };
            grid.Children.Add(ring);
            if (!talkRings.TryGetValue(talkUser, out var list)) talkRings[talkUser] = list = [];
            list.Add(ring);
        }
        if (online is bool on)
        {
            double dot = Math.Max(8, size * 0.32);
            grid.Children.Add(new Ellipse { Width = dot, Height = dot, Fill = on ? (Brush)FindResource("Accent") : Brush("#4A4A4A"), Stroke = Brush("#0B0B0B"), StrokeThickness = Math.Max(2, size / 14), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -2, -2) });
        }
        return grid;
    }
    private Button Small(string label, Action action, bool primary = false)
    {
        var button = Button(label, action, primary); button.Padding = new Thickness(10, 6, 10, 6); button.FontSize = 12; button.Margin = new Thickness(0, 0, 6, 6); return button;
    }
    private static string Glyph(string kind) => kind switch { ChannelKind.Voice => "🔊", ChannelKind.Board => "▦", _ => "#" };

    // ===================== Trilho de servidores =====================
    private void RenderRail()
    {
        Rail.Children.Clear();
        bool home = page != PageGroups;
        Rail.Children.Add(RailIcon(LogoMark(), "Ximbuismo · Boost", home, false, () => Navigate(PageOverview)));
        Rail.Children.Add(new Border { Height = 2, Width = 32, Background = Brush("#262626"), Margin = new Thickness(0, 4, 0, 8), HorizontalAlignment = HorizontalAlignment.Center });
        foreach (var g in groups)
        {
            bool selected = !home && !addServer && current?.Id == g.Id;
            bool hasUnread = unread.Values.Any(u => u.Group == g.Id && u.Count > 0);
            bool live = voiceStates.Count > 0 && callGroup?.Id == g.Id;
            var label = new TextBlock { Text = InitialsOf(g.Name), FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = (FontFamily)FindResource("Mono") };
            Rail.Children.Add(RailIcon(label, g.Name + (live ? " · você está na voz" : ""), selected, hasUnread, () => Try(() => SelectGroup(g))));
        }
        var plus = new TextBlock { Text = "+", FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -3, 0, 0) };
        Rail.Children.Add(RailIcon(plus, team.User is null ? "Entrar para usar servidores" : "Criar ou entrar em um servidor", !home && addServer, false, () =>
        {
            if (team.User is null) { ShowLogin(); return; }
            addServer = true; Navigate(PageGroups);
        }, accent: true));
    }
    private static UIElement LogoMark()
    {
        return new Image { Source = (ImageSource)Application.Current.FindResource("BrandLogo"), Width = 34, Height = 34 };
    }
    // Ícone circular que vira quadrado arredondado no hover/seleção, com a "pílula" branca à esquerda (Discord).
    private UIElement RailIcon(UIElement content, string tip, bool selected, bool unreadDot, Action click, bool accent = false)
    {
        var pill = new Border { Width = 4, Height = selected ? 40 : unreadDot ? 8 : 0, Background = Brushes.White, CornerRadius = new CornerRadius(0, 4, 4, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        var icon = new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(selected ? 14 : 24), Background = selected ? Brush(content is Image ? "#222222" : "#EDEDED") : Brush("#161616"),
            BorderBrush = Brush(accent ? "#2E2E2E" : "#222222"), BorderThickness = new Thickness(1), Child = content, HorizontalAlignment = HorizontalAlignment.Center, Cursor = Cursors.Hand, ToolTip = tip };
        System.Windows.Documents.TextElement.SetForeground(icon, selected ? Brushes.Black : accent ? (Brush)FindResource("Accent") : Brush("#EDEDED"));
        if (selected && content is Grid g) foreach (var shape in g.Children.OfType<Shape>()) shape.Stroke = Brushes.Black;
        var host = new Grid { Height = 56, Background = Brushes.Transparent };
        host.Children.Add(pill); host.Children.Add(icon);
        host.MouseEnter += (_, _) => { if (!selected) { icon.CornerRadius = new CornerRadius(14); icon.Background = Brush("#222222"); pill.Height = Math.Max(pill.Height, 20); } };
        host.MouseLeave += (_, _) => { if (!selected) { icon.CornerRadius = new CornerRadius(24); icon.Background = Brush("#161616"); pill.Height = unreadDot ? 8 : 0; } };
        host.MouseLeftButtonUp += (_, _) => click();
        return host;
    }

    // ===================== Barra lateral (canais ou Boost) =====================
    private void RenderSidebar()
    {
        if (SidebarHost is null) return;
        SidebarHost.Children.Clear(); ServerMenu.IsOpen = false;
        foreach (var list in talkRings.Values) list.RemoveAll(r => IsInSidebar(r));
        bool server = page == PageGroups && current is not null && !addServer && team.User is not null;
        SidebarChevron.Visibility = server ? Visibility.Visible : Visibility.Collapsed;
        SidebarHeader.Cursor = server ? Cursors.Hand : null;
        if (!server) { RenderHomeSidebar(); return; }
        SidebarTitle.Text = current!.Name;
        bool owner = current.OwnerId == team.User?.Id;
        foreach (var (kind, title) in new[] { (ChannelKind.Text, "CANAIS DE TEXTO"), (ChannelKind.Board, "QUADROS"), (ChannelKind.Voice, "CANAIS DE VOZ") })
        {
            var head = new DockPanel { Margin = new Thickness(8, 14, 6, 4) };
            if (owner)
            {
                var add = new Button { Content = Mono("+", 13, "#8C8C8C"), Style = (Style)FindResource("GhostButton"), Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(0), ToolTip = "Criar canal" };
                string k = kind; add.Click += (_, _) => { creatingKind = k; RenderSidebar(); };
                DockPanel.SetDock(add, Dock.Right); head.Children.Add(add);
            }
            head.Children.Add(Mono(title, 10, "#6A6A6A"));
            SidebarHost.Children.Add(head);
            foreach (var c in channels.Where(c => c.Kind == kind)) SidebarHost.Children.Add(ChannelRow(c, owner));
            if (creatingKind == kind) SidebarHost.Children.Add(NewChannelBox(kind));
        }
    }
    private bool IsInSidebar(DependencyObject element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)) if (ReferenceEquals(node, SidebarHost)) return true;
        return false;
    }
    private UIElement ChannelRow(ChannelDto c, bool owner)
    {
        bool selected = channel?.Id == c.Id;
        int count = unread.TryGetValue(c.Id, out var u) ? u.Count : 0;
        var row = new DockPanel();
        var glyph = new TextBlock { Text = Glyph(c.Kind), Width = 22, FontSize = c.Kind == ChannelKind.Voice ? 12 : 15, Foreground = Brush("#6A6A6A"), VerticalAlignment = VerticalAlignment.Center, FontFamily = c.Kind == ChannelKind.Voice ? new FontFamily("Segoe UI Emoji") : (FontFamily)FindResource("Sans") };
        row.Children.Add(glyph);
        Button? delete = null;
        if (owner)
        {
            bool armed = false;
            delete = new Button { Content = Mono("✕", 10, "#6A6A6A"), Style = (Style)FindResource("GhostButton"), Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(0), Visibility = Visibility.Hidden, ToolTip = "Apagar canal" };
            delete.Click += (_, _) =>
            {
                if (!armed) { armed = true; delete.Content = Mono("APAGAR?", 9.5, "#F0645A"); return; }
                Try(() => team.DeleteChannel(c.Id));
            };
            DockPanel.SetDock(delete, Dock.Right); row.Children.Add(delete);
        }
        if (count > 0) { var badge = new Border { Background = (Brush)FindResource("Danger"), CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 1), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = count > 99 ? "99+" : count.ToString(), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White } }; DockPanel.SetDock(badge, Dock.Right); row.Children.Add(badge); }
        row.Children.Add(new TextBlock { Text = c.Name, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontWeight = count > 0 || selected ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Brush(selected || count > 0 ? "#FFFFFF" : "#8C8C8C") });
        var item = new Border { Child = row, Padding = new Thickness(8, 6, 6, 6), Margin = new Thickness(0, 1, 0, 1), Background = selected ? Brush("#1E1E1E") : Brushes.Transparent, Cursor = Cursors.Hand, CornerRadius = new CornerRadius(4) };
        item.MouseEnter += (_, _) => { if (!selected) item.Background = Brush("#171717"); if (delete is not null) delete.Visibility = Visibility.Visible; };
        item.MouseLeave += (_, _) => { if (!selected) item.Background = Brushes.Transparent; if (delete is not null) delete.Visibility = Visibility.Hidden; };
        item.MouseLeftButtonUp += (_, _) => Try(() => SelectChannel(c, join: c.Kind == ChannelKind.Voice));
        if (c.Kind != ChannelKind.Voice) return item;
        // Quem está no canal de voz aparece logo abaixo dele.
        var stack = Stack(item);
        foreach (var p in voiceStates.GetValueOrDefault(c.Id) ?? [])
        {
            var who = new DockPanel { Margin = new Thickness(30, 2, 6, 4) };
            if (p.Muted) { var m = new TextBlock { Text = "🔇", FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 11, Foreground = (Brush)FindResource("Danger"), VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(m, Dock.Right); who.Children.Add(m); }
            var avatar = Avatar(p.Name, p.UserId, 22, null, p.UserId); avatar.Margin = new Thickness(0, 0, 8, 0); who.Children.Add(avatar);
            who.Children.Add(new TextBlock { Text = p.Name, FontSize = 12.5, Foreground = Brush("#AFAFAF"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(who);
        }
        return stack;
    }
    private UIElement NewChannelBox(string kind)
    {
        var box = new TextBox { MaxLength = 40, Margin = new Thickness(8, 4, 8, 8), ToolTip = "Enter cria · Esc cancela" };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { creatingKind = null; RenderSidebar(); }
            if (e.Key != Key.Enter || current is null) return;
            string name = box.Text; long group = current.Id; creatingKind = null;
            Try(async () => { var created = await team.CreateChannel(group, name, kind); channels = await team.Channels(group); await SelectChannel(created, join: false); });
        };
        Dispatcher.BeginInvoke(() => box.Focus(), DispatcherPriority.Input);
        return Stack(Mono(kind == ChannelKind.Voice ? "NOME DO CANAL DE VOZ" : kind == ChannelKind.Board ? "NOME DO QUADRO" : "NOME DO CANAL", 9.5, "#5C5C5C") is var l && (l.Margin = new Thickness(10, 4, 0, 0)) != default ? l : l, box);
    }
    private void RenderHomeSidebar()
    {
        SidebarTitle.Text = "Boost";
        SidebarHost.Children.Add(HomeRow("◆", "Boost", page == PageOverview, () => Navigate(PageOverview)));
        SidebarHost.Children.Add(HomeRow("◎", "Servidores", false, () => { if (team.User is null) ShowLogin(); else Try(() => LoadGroups(current?.Id)); }));
        var head = Mono(games.Count == 0 ? "JOGOS" : $"JOGOS — {games.Count}", 10, "#6A6A6A"); head.Margin = new Thickness(8, 18, 0, 6); SidebarHost.Children.Add(head);
        foreach (var game in games)
        {
            var g = game;
            var row = new DockPanel();
            var art = new Border { Width = 26, Height = 26, Background = Brush("#1A1A1A"), Margin = new Thickness(0, 0, 10, 0), ClipToBounds = true, CornerRadius = new CornerRadius(4) };
            if (gameArt.TryGetValue(g.Key, out var a) && (a.Color ?? a.Gray) is ImageSource src) art.Child = new Image { Source = src, Stretch = Stretch.UniformToFill };
            else art.Child = new TextBlock { Text = Initials(g.Name), FontSize = 9, Foreground = Brush("#8C8C8C"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(art);
            row.Children.Add(new TextBlock { Text = g.Name, FontSize = 13, Foreground = Brush("#AFAFAF"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var item = new Border { Child = row, Padding = new Thickness(8, 5, 6, 5), Cursor = Cursors.Hand, CornerRadius = new CornerRadius(4), Background = Brushes.Transparent, ToolTip = "Boost + jogar" };
            item.MouseEnter += (_, _) => item.Background = Brush("#171717"); item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
            item.MouseLeftButtonUp += (_, _) => Try(() => BoostGame(g));
            SidebarHost.Children.Add(item);
        }
    }
    private UIElement HomeRow(string icon, string label, bool selected, Action click)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = icon, Width = 26, FontSize = 14, Foreground = Brush(selected ? "#FFFFFF" : "#8C8C8C"), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = label, FontSize = 14, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Brush(selected ? "#FFFFFF" : "#AFAFAF"), VerticalAlignment = VerticalAlignment.Center });
        var item = new Border { Child = row, Padding = new Thickness(10, 9, 8, 9), Margin = new Thickness(0, 1, 0, 1), Background = selected ? Brush("#1E1E1E") : Brushes.Transparent, Cursor = Cursors.Hand, CornerRadius = new CornerRadius(4) };
        item.MouseEnter += (_, _) => { if (!selected) item.Background = Brush("#171717"); }; item.MouseLeave += (_, _) => { if (!selected) item.Background = Brushes.Transparent; };
        item.MouseLeftButtonUp += (_, _) => click();
        return item;
    }
    // Menu do servidor (clique no nome): convite, criar canais, sair.
    private void SidebarHeaderClicked(object sender, MouseButtonEventArgs e)
    {
        if (page != PageGroups || current is null || addServer) return;
        ServerMenuHost.Children.Clear();
        var group = current; bool owner = group.OwnerId == team.User?.Id;
        Button Item(string text, string color, Action action)
        {
            var b = new Button { Content = new TextBlock { Text = text, Foreground = Brush(color), FontSize = 13 }, Style = (Style)FindResource("GhostButton"), HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0) };
            b.Click += (_, _) => action(); return b;
        }
        ServerMenuHost.Children.Add(Item($"Convidar pessoas · {group.Code}", "#9AB8FF", () => { try { Clipboard.SetText(group.Code); } catch (Exception) { } ServerMenu.IsOpen = false; Status($"Código de convite {group.Code} copiado. Quem receber clica em + e cola o código."); }));
        if (owner)
        {
            ServerMenuHost.Children.Add(Item("Criar canal de texto", "#EDEDED", () => { creatingKind = ChannelKind.Text; RenderSidebar(); }));
            ServerMenuHost.Children.Add(Item("Criar quadro de ideias", "#EDEDED", () => { creatingKind = ChannelKind.Board; RenderSidebar(); }));
            ServerMenuHost.Children.Add(Item("Criar canal de voz", "#EDEDED", () => { creatingKind = ChannelKind.Voice; RenderSidebar(); }));
        }
        ServerMenuHost.Children.Add(new Border { Height = 1, Background = Brush("#262626"), Margin = new Thickness(6, 4, 6, 4) });
        bool armed = false; Button? leave = null;
        leave = Item("Sair do servidor", "#F0645A", () =>
        {
            if (!armed) { armed = true; ((TextBlock)leave!.Content).Text = "Clique de novo para sair"; return; }
            ServerMenu.IsOpen = false;
            Try(async () => { await team.LeaveGroup(group.Id); if (callGroup?.Id == group.Id) await LeaveCall(); current = null; await LoadGroups(null); Status($"Você saiu de {group.Name}."); });
        });
        ServerMenuHost.Children.Add(leave);
        ServerMenu.IsOpen = true;
    }

    // ===================== Navegação entre servidores e canais =====================
    private async Task LoadGroups(long? select, bool navigate = true)
    {
        groups = await team.Groups();
        var target = groups.FirstOrDefault(g => g.Id == select) ?? (current is null ? null : groups.FirstOrDefault(g => g.Id == current.Id)) ?? groups.FirstOrDefault();
        if (target is null) { current = null; channel = null; addServer = true; if (navigate) Navigate(PageGroups); else RenderRail(); return; }
        if (navigate) await SelectGroup(target); else RenderRail();
    }
    private async Task SelectGroup(GroupDto group)
    {
        addServer = false;
        var list = team.Channels(group.Id); var people = team.Members(group.Id); var calls = team.CallStates(group.Id);
        await Task.WhenAll(list, people, calls);
        current = group; channels = list.Result; members = people.Result;
        foreach (var c in channels.Where(c => c.Kind == ChannelKind.Voice)) voiceStates[c.Id] = calls.Result.FirstOrDefault(v => v.ChannelId == c.Id)?.Participants ?? [];
        var target = channels.FirstOrDefault(c => lastChannel.TryGetValue(group.Id, out var id) && c.Id == id && c.Kind != ChannelKind.Voice)
            ?? channels.First(c => c.Kind == ChannelKind.Text);
        await SelectChannel(target, join: false);
    }
    private async Task SelectChannel(ChannelDto target, bool join = false)
    {
        editingIdea = null; newIdeaStatus = null;
        if (target.Kind == ChannelKind.Text) { chat = await team.History(target.Id); chatHasMore = chat.Count >= 80; unread.Remove(target.Id); }
        else if (target.Kind == ChannelKind.Board) ideas = await team.Ideas(target.Id);
        channel = target; if (target.Kind != ChannelKind.Voice) lastChannel[target.GroupId] = target.Id;
        if (join && callChannel?.Id != target.Id && current is not null) await JoinCall(target, current);
        Navigate(PageGroups);
    }

    // ===================== Conteúdo =====================
    private void ClearTeamView()
    {
        chatPanel = membersPanel = null; chatScroll = null; boardHost = voiceHost = null; chatInput = null;
        talkRings.Clear(); FullHost.Children.Clear();
    }
    private void GroupsPage()
    {
        TitleDivider.Visibility = Visibility.Visible;
        if (team.User is null)
        {
            PageEyebrow.Text = "◎"; PageTitle.Text = "Servidores"; PageSubtitle.Text = "Converse, chame para a voz e organize ideias.";
            FullHost.Children.Add(Centered(Mono("SERVIDORES • OFFLINE", 11, "#EDEDED"), Text("Entre para usar servidores.", 20, true),
                Muted("Crie uma conta para conversar, entrar em canais de voz e organizar ideias e tarefas com sua turma."), Actions(Button("Entrar ou criar conta", ShowLogin, true))));
            return;
        }
        if (addServer || current is null || channel is null) { AddServerPage(); return; }
        PageEyebrow.Text = Glyph(channel.Kind); PageEyebrow.FontFamily = channel.Kind == ChannelKind.Voice ? new FontFamily("Segoe UI Emoji") : (FontFamily)FindResource("Sans");
        PageTitle.Text = channel.Name;
        PageSubtitle.Text = channel.Kind switch { ChannelKind.Voice => "Canal de voz", ChannelKind.Board => "Quadro de ideias e tarefas", _ => $"Chat do servidor {current.Name}" };
        var invite = new Button { Content = Mono("CONVIDAR · " + current.Code, 10, "#9AB8FF"), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 0), ToolTip = "Copiar código de convite" };
        string code = current.Code; invite.Click += (_, _) => { try { Clipboard.SetText(code); Status($"Código {code} copiado."); } catch (Exception ex) { Status(ex.Message); } };
        HeaderActions.Children.Add(invite);
        var people = new Button { Content = new TextBlock { Text = "👥", FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 14, Foreground = Brush(membersVisible ? "#FFFFFF" : "#6A6A6A") }, Style = (Style)FindResource("GhostButton"), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0), ToolTip = "Mostrar ou esconder membros" };
        people.Click += (_, _) => { membersVisible = !membersVisible; Navigate(PageGroups); };
        HeaderActions.Children.Add(people);

        var grid = new Grid(); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = membersVisible ? new GridLength(240) : new GridLength(0) });
        var content = new Grid(); grid.Children.Add(content);
        membersPanel = new StackPanel { Margin = new Thickness(12, 16, 12, 16) };
        var right = new Border { Background = Brush("#0E0E0E"), BorderBrush = Brush("#262626"), BorderThickness = new Thickness(1, 0, 0, 0), Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = membersPanel } };
        Grid.SetColumn(right, 1); grid.Children.Add(right);
        FullHost.Children.Add(grid);
        switch (channel.Kind)
        {
            case ChannelKind.Voice: voiceHost = content; RenderVoice(); break;
            case ChannelKind.Board: boardHost = new Grid { Margin = new Thickness(16) }; content.Children.Add(boardHost); RenderBoard(keepEditor: false); break;
            default: BuildChat(content); break;
        }
        RenderMembers();
    }
    private UIElement Centered(params UIElement[] items)
    {
        var art = new Image { Stretch = Stretch.None, Height = 260 };
        art.Loaded += (_, _) => art.Source = DotArt.Sphere(520, 260, 0.6, 0.35, 11);
        var box = Stack(new UIElement[] { art }.Concat(items).ToArray());
        box.MaxWidth = 540; box.HorizontalAlignment = HorizontalAlignment.Center; box.VerticalAlignment = VerticalAlignment.Center; box.Margin = new Thickness(24);
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = box };
    }
    private void AddServerPage()
    {
        PageEyebrow.Text = "+"; PageTitle.Text = "Criar ou entrar em um servidor"; PageSubtitle.Text = "Um servidor tem canais de texto, quadros de ideias e canais de voz.";
        var name = new TextBox { MaxLength = 60, Margin = new Thickness(0, 6, 0, 10) };
        var code = new TextBox { MaxLength = 12, CharacterCasing = CharacterCasing.Upper, FontFamily = (FontFamily)FindResource("Mono"), Margin = new Thickness(0, 6, 0, 10) };
        async Task Create()
        {
            if (name.Text.Trim().Length == 0) { Status("Dê um nome ao servidor."); return; }
            var group = await team.CreateGroup(name.Text); await LoadGroups(group.Id);
            Status($"Servidor criado com #geral, #ideias e o canal de voz Geral. Convide com o código {group.Code}.");
        }
        async Task Join()
        {
            if (code.Text.Trim().Length == 0) { Status("Cole o código de convite."); return; }
            var group = await team.JoinGroup(code.Text); await LoadGroups(group.Id); Status($"Você entrou em {group.Name}.");
        }
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) Try(Create); };
        code.KeyDown += (_, e) => { if (e.Key == Key.Enter) Try(Join); };
        var cards = new UniformGrid { Columns = 2 };
        var create = Card(Stack(Mono("CRIAR", 11, "#EDEDED"), Text("Seu próprio servidor", 17, true), Muted("Começa com #geral, um quadro #ideias e o canal de voz Geral."), Mono("NOME DO SERVIDOR", 10, "#5C5C5C") is var l1 ? Spaced(l1, 0, 14, 0, 0) : l1, name, Actions(Button("Criar servidor", () => Try(Create), true))));
        create.Margin = new Thickness(0, 0, 8, 0);
        var join = Card(Stack(Mono("ENTRAR", 11, "#EDEDED"), Text("Tenho um convite", 17, true), Muted("Peça o código de 6 letras para quem está no servidor."), Mono("CÓDIGO DE CONVITE", 10, "#5C5C5C") is var l2 ? Spaced(l2, 0, 14, 0, 0) : l2, code, Actions(Button("Entrar no servidor", () => Try(Join)))));
        join.Margin = new Thickness(8, 0, 0, 0);
        cards.Children.Add(create); cards.Children.Add(join);
        FullHost.Children.Add(Centered(cards));
        Dispatcher.BeginInvoke(() => name.Focus(), DispatcherPriority.Input);
    }
    private static T Spaced<T>(T element, double l, double t, double r, double b) where T : FrameworkElement { element.Margin = new Thickness(l, t, r, b); return element; }

    // ---------- Chat de texto ----------
    private void BuildChat(Grid host)
    {
        host.RowDefinitions.Add(new()); host.RowDefinitions.Add(new() { Height = GridLength.Auto });
        chatPanel = new StackPanel { Margin = new Thickness(0, 12, 0, 12) };
        chatScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = chatPanel }; host.Children.Add(chatScroll);
        chatInput = new TextBox { MaxLength = 4000, TextWrapping = TextWrapping.Wrap, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(14, 12, 14, 12), BorderThickness = new Thickness(0), Background = Brush("#161616") };
        var hint = new TextBlock { Text = $"Conversar em #{channel!.Name}", Foreground = Brush("#5C5C5C"), Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        chatInput.TextChanged += (_, _) => hint.Visibility = chatInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        chatInput.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { e.Handled = true; SendChat(); } };
        var box = new Grid { Margin = new Thickness(16, 0, 16, 18) }; box.Children.Add(chatInput); box.Children.Add(hint);
        Grid.SetRow(box, 1); host.Children.Add(box);
        RenderChat();
        Dispatcher.BeginInvoke(() => chatInput?.Focus(), DispatcherPriority.Input);
    }
    private void SendChat()
    {
        if (channel is null || chatInput is null) return;
        string text = chatInput.Text.Trim(); if (text.Length == 0) return;
        long id = channel.Id; chatInput.Clear();
        Try(async () => { try { await team.Send(id, text); } catch { if (chatInput is not null && chatInput.Text.Length == 0) chatInput.Text = text; throw; } });
    }
    private void RenderChat(bool scrollToEnd = true)
    {
        if (chatPanel is null || channel is null) return;
        double fromEnd = chatScroll is null ? 0 : chatScroll.ExtentHeight - chatScroll.VerticalOffset;
        chatPanel.Children.Clear();
        if (chatHasMore)
        {
            var more = Small("Carregar mensagens anteriores", () => Try(async () =>
            {
                if (channel is null || chat.Count == 0) return; long id = channel.Id;
                var older = await team.History(id, chat[0].Id); if (channel?.Id != id) return;
                chatHasMore = older.Count >= 80; chat.InsertRange(0, older); RenderChat(scrollToEnd: false);
            }));
            more.HorizontalAlignment = HorizontalAlignment.Center; chatPanel.Children.Add(more);
        }
        else
        {
            // Começo do canal, como no Discord.
            var welcome = new StackPanel { Margin = new Thickness(18, 18, 18, 18) };
            welcome.Children.Add(new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(32), Background = Brush("#1E1E1E"), HorizontalAlignment = HorizontalAlignment.Left, Child = new TextBlock { Text = "#", FontSize = 34, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -4, 0, 0) } });
            welcome.Children.Add(Spaced(Text($"Bem-vindo(a) a #{channel.Name}!", 24, true), 0, 12, 0, 4));
            welcome.Children.Add(Muted($"Este é o começo do canal #{channel.Name}.", 13));
            chatPanel.Children.Add(welcome);
        }
        for (int i = 0; i < chat.Count; i++) chatPanel.Children.Add(MessageView(chat[i], i > 0 ? chat[i - 1] : null));
        if (chatScroll is null) return;
        if (scrollToEnd) chatScroll.ScrollToEnd();
        else { chatScroll.UpdateLayout(); chatScroll.ScrollToVerticalOffset(chatScroll.ExtentHeight - fromEnd); }
    }
    private UIElement MessageView(MessageDto m, MessageDto? previous)
    {
        bool continued = previous is not null && previous.UserId == m.UserId && (m.At - previous.At) < TimeSpan.FromMinutes(5);
        var body = new TextBox { Text = m.Text, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(-2, 0, 0, 0), TextWrapping = TextWrapping.Wrap, FontSize = 14, Foreground = Brush("#DCDCDC"), VerticalContentAlignment = VerticalAlignment.Top };
        var local = m.At.ToLocalTime();
        var row = new Grid { Background = Brushes.Transparent, Margin = new Thickness(0, continued ? 0 : 14, 0, 0) };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(72) }); row.ColumnDefinitions.Add(new());
        row.MouseEnter += (_, _) => row.Background = Brush("#121212"); row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        if (continued)
        {
            var time = Mono(local.ToString("HH:mm"), 9.5, "#3A3A3A"); time.HorizontalAlignment = HorizontalAlignment.Center; time.VerticalAlignment = VerticalAlignment.Center; time.Visibility = Visibility.Hidden;
            row.MouseEnter += (_, _) => time.Visibility = Visibility.Visible; row.MouseLeave += (_, _) => time.Visibility = Visibility.Hidden;
            row.Children.Add(time);
            var b = Spaced(body, 0, 1, 18, 1); Grid.SetColumn(b, 1); row.Children.Add(b); return row;
        }
        var avatar = Avatar(m.Author, m.UserId, 40, null); avatar.VerticalAlignment = VerticalAlignment.Top; avatar.Margin = new Thickness(0, 2, 0, 0); row.Children.Add(avatar);
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock { Text = m.Author, FontWeight = FontWeights.SemiBold, FontSize = 14, Foreground = Brushes.White });
        string when = local.Date == DateTime.Today ? "Hoje às " + local.ToString("HH:mm") : local.Date == DateTime.Today.AddDays(-1) ? "Ontem às " + local.ToString("HH:mm") : local.ToString("dd/MM/yyyy HH:mm");
        head.Children.Add(Spaced(new TextBlock { Text = when, FontSize = 11, Foreground = Brush("#6A6A6A"), VerticalAlignment = VerticalAlignment.Bottom }, 8, 0, 0, 1));
        var text = Stack(head, Spaced(body, 0, 2, 18, 2)); Grid.SetColumn(text, 1); row.Children.Add(text);
        return row;
    }
    private void OnMessage(MessageDto m)
    {
        if (channel?.Id == m.ChannelId)
        {
            if (chat.Any(x => x.Id == m.Id)) return;
            var previous = chat.LastOrDefault(); chat.Add(m);
            if (chatPanel is not null)
            {
                bool atEnd = chatScroll is null || chatScroll.VerticalOffset >= chatScroll.ScrollableHeight - 60;
                chatPanel.Children.Add(MessageView(m, previous)); if (atEnd || m.UserId == team.User?.Id) chatScroll?.ScrollToEnd();
            }
            if (page == PageGroups && IsActive) return;
        }
        if (m.UserId == team.User?.Id) return;
        unread[m.ChannelId] = (m.GroupId, (unread.TryGetValue(m.ChannelId, out var u) ? u.Count : 0) + 1);
        RenderRail(); if (page == PageGroups && current?.Id == m.GroupId) RenderSidebar();
    }

    // ---------- Canal de voz: blocos dos participantes ----------
    private void RenderVoice()
    {
        if (voiceHost is null || channel is null || current is null) return;
        foreach (var list in talkRings.Values) list.RemoveAll(r => !IsInSidebar(r));
        voiceHost.Children.Clear(); voiceHost.RowDefinitions.Clear();
        voiceHost.RowDefinitions.Add(new()); voiceHost.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var people = voiceStates.GetValueOrDefault(channel.Id) ?? [];
        bool here = callChannel?.Id == channel.Id;
        if (people.Count == 0)
        {
            var empty = Centered(Mono("CANAL DE VOZ", 11, "#EDEDED"), Text($"Ninguém em {channel.Name} ainda.", 20, true), Muted("Entre e chame a galera. O áudio só vai para quem está neste canal."));
            voiceHost.Children.Add(empty);
        }
        else
        {
            var tiles = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16) };
            foreach (var p in people)
            {
                var avatar = Avatar(p.Name, p.UserId, 80, null, p.UserId); avatar.HorizontalAlignment = HorizontalAlignment.Center; avatar.VerticalAlignment = VerticalAlignment.Center;
                var tile = new Grid { Width = 280, Height = 170, Margin = new Thickness(8) };
                tile.Children.Add(new Border { Background = Brush("#141414"), BorderBrush = Brush("#262626"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) });
                tile.Children.Add(avatar);
                var name = new Border { Background = Brush("#000000"), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10),
                    Child = new TextBlock { Text = (p.Muted ? "🔇 " : "") + p.Name + (p.UserId == team.User?.Id ? " (você)" : ""), FontSize = 12, Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI, Segoe UI Emoji") } };
                tile.Children.Add(name);
                tiles.Children.Add(tile);
            }
            voiceHost.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = tiles });
        }
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 22) };
        if (!here) bar.Children.Add(Small(callChannel is null ? "Entrar na chamada" : $"Trocar para {channel.Name}", () => Try(() => JoinCall(channel, current)), true));
        else
        {
            bar.Children.Add(Small(micMuted ? "🎙 Ativar microfone" : "🎙 Mudo", () => { micMuted = !micMuted; Try(ApplyVoiceState); }));
            bar.Children.Add(Small(deafened ? "🎧 Ativar áudio" : "🎧 Ensurdecer", () => { deafened = !deafened; Try(ApplyVoiceState); }));
            var leave = Small("Desconectar", () => Try(LeaveCall)); leave.Background = (Brush)FindResource("Danger"); leave.BorderBrush = (Brush)FindResource("Danger"); leave.Foreground = Brushes.White; bar.Children.Add(leave);
            if (!VoiceCall.HasMicrophone) bar.Children.Add(Spaced(Mono("SEM MICROFONE · SÓ OUVINDO", 9.5, "#5C5C5C"), 8, 8, 0, 0));
        }
        Grid.SetRow(bar, 1); voiceHost.Children.Add(bar);
    }
    private async Task JoinCall(ChannelDto target, GroupDto group)
    {
        if (callChannel is not null && callChannel.Id != target.Id) await LeaveCall();
        if (voice is null)
        {
            var created = new VoiceCall(team.SendAudio) { Muted = micMuted || deafened, Deafened = deafened };
            try { created.Start(); } catch (Exception ex) { created.Dispose(); throw new InvalidOperationException("Não foi possível abrir o áudio: " + ex.Message); }
            voice = created;
        }
        try { voiceStates[target.Id] = await team.JoinCall(target.Id, micMuted || deafened); }
        catch { voice?.Dispose(); voice = null; throw; }
        callChannel = target; callGroup = group; voiceTimer.Start();
        if (!VoiceCall.HasMicrophone) Status("Nenhum microfone encontrado: você só ouve.");
        UpdateAccount();
    }
    private async Task LeaveCall()
    {
        if (callChannel is null && voice is null) return;
        var left = callChannel; callChannel = null; callGroup = null; voiceTimer.Stop();
        voice?.Dispose(); voice = null;
        try { await team.LeaveCall(); } catch (Exception) { }
        if (left is not null && team.User is not null) voiceStates[left.Id] = (voiceStates.GetValueOrDefault(left.Id) ?? []).Where(p => p.UserId != team.User.Id).ToList();
        UpdateAccount();
        if (page == PageGroups) { RenderSidebar(); RenderVoice(); }
    }
    private void UpdateTalking()
    {
        if (voice is null) return;
        foreach (var (user, rings) in talkRings)
        {
            bool talking = user == team.User?.Id ? voice.Speaking : voice.IsTalking(user);
            foreach (var ring in rings) ring.Visibility = talking ? Visibility.Visible : Visibility.Hidden;
        }
    }

    // ---------- Membros ----------
    private void RenderMembers()
    {
        if (membersPanel is null || current is null) return; membersPanel.Children.Clear();
        foreach (var (title, list) in new[] { ("ONLINE", members.Where(m => m.Online).ToList()), ("OFFLINE", members.Where(m => !m.Online).ToList()) })
        {
            if (list.Count == 0) continue;
            membersPanel.Children.Add(Spaced(Mono($"{title} — {list.Count}", 10, "#6A6A6A"), 8, membersPanel.Children.Count == 0 ? 0 : 18, 0, 8));
            foreach (var m in list.OrderBy(m => m.Name))
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
                var avatar = Avatar(m.Name, m.UserId, 32, m.Online); avatar.Margin = new Thickness(0, 0, 10, 0); row.Children.Add(avatar);
                var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(new TextBlock { Text = m.Name, FontSize = 13.5, Foreground = Brush(m.Online ? "#EDEDED" : "#6A6A6A"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 140 });
                if (m.Role == "dono") line.Children.Add(Spaced(new TextBlock { Text = "♛", Foreground = (Brush)FindResource("Warn"), FontSize = 12, ToolTip = "Dono do servidor" }, 6, 0, 0, 0));
                name.Children.Add(line);
                if (m.UserId == team.User?.Id) name.Children.Add(Mono("VOCÊ", 9, "#4A4A4A"));
                row.Children.Add(name);
                var item = new Border { Child = row, Padding = new Thickness(8, 5, 8, 5), CornerRadius = new CornerRadius(4), Background = Brushes.Transparent, Opacity = m.Online ? 1 : 0.6, ToolTip = m.Email };
                item.MouseEnter += (_, _) => item.Background = Brush("#171717"); item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
                membersPanel.Children.Add(item);
            }
        }
    }

    // ---------- Quadro de ideias e tarefas ----------
    private static string KindLabel(string kind) => kind switch { IdeaKind.Task => "TAREFA", IdeaKind.Bug => "PROBLEMA", _ => "IDEIA" };
    private static string KindColor(string kind) => kind switch { IdeaKind.Task => "#9AB8FF", IdeaKind.Bug => "#F0645A", _ => "#4ADE80" };
    private void RenderBoard(bool keepEditor)
    {
        if (!keepEditor) { editingIdea = null; newIdeaStatus = null; }
        if (boardHost is null) return;
        boardHost.Children.Clear(); boardHost.ColumnDefinitions.Clear();
        for (int i = 0; i < 3; i++) boardHost.ColumnDefinitions.Add(new());
        for (int c = 0; c < 3; c++)
        {
            string status = IdeaStatus.All[c];
            var list = ideas.Where(i => i.Status == status).OrderByDescending(i => i.Priority == IdeaPriority.High).ThenByDescending(i => i.Votes).ThenByDescending(i => i.Updated).ToList();
            var dock = new DockPanel { Margin = new Thickness(c == 0 ? 0 : 6, 0, 6, 0) };
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var add = new Button { Content = Mono("+ NOVO", 10, "#EDEDED"), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0) };
            add.Click += (_, _) => { editingIdea = null; newIdeaStatus = status; RenderBoard(true); };
            DockPanel.SetDock(add, Dock.Right); head.Children.Add(add);
            var label = Mono($"{IdeaStatus.Label(status)} · {list.Count}", 10.5, "#EDEDED"); label.VerticalAlignment = VerticalAlignment.Center; label.TextWrapping = TextWrapping.NoWrap; label.TextTrimming = TextTrimming.CharacterEllipsis; head.Children.Add(label);
            DockPanel.SetDock(head, Dock.Top); dock.Children.Add(head);
            var cards = new StackPanel();
            foreach (var idea in list) cards.Children.Add(IdeaCard(idea));
            if (list.Count == 0) { var empty = new Border { BorderBrush = Brush("#262626"), BorderThickness = new Thickness(1), Padding = new Thickness(12, 18, 12, 18), CornerRadius = new CornerRadius(6), Child = Mono("VAZIO", 10, "#3A3A3A") }; ((TextBlock)empty.Child).HorizontalAlignment = HorizontalAlignment.Center; cards.Children.Add(empty); }
            dock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = cards });
            Grid.SetColumn(dock, c); boardHost.Children.Add(dock);
        }
        if (editingIdea is not null || newIdeaStatus is not null)
        {
            var panel = (FrameworkElement)IdeaEditor(); Grid.SetColumnSpan(panel, 3);
            panel.HorizontalAlignment = HorizontalAlignment.Right; panel.Width = 320; panel.MaxWidth = Math.Max(240, boardHost.ActualWidth - 40);
            boardHost.Children.Add(panel);
        }
    }
    private UIElement IdeaCard(IdeaDto idea)
    {
        bool selected = editingIdea?.Id == idea.Id;
        var top = new DockPanel();
        if (idea.Priority != IdeaPriority.Normal) { var p = Mono(idea.Priority == IdeaPriority.High ? "▲ ALTA" : "▽ BAIXA", 9.5, idea.Priority == IdeaPriority.High ? "#E3A33B" : "#5C5C5C"); DockPanel.SetDock(p, Dock.Right); top.Children.Add(p); }
        top.Children.Add(Mono(KindLabel(idea.Kind), 9.5, KindColor(idea.Kind)));
        var title = new TextBlock { Text = idea.Title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), TextDecorations = idea.Status == IdeaStatus.Done ? TextDecorations.Strikethrough : null, Foreground = idea.Status == IdeaStatus.Done ? Brush("#8C8C8C") : Brush("#EDEDED") };
        var stack = Stack(top, title);
        if (idea.Body.Length > 0) { var body = Muted(idea.Body.Length > 140 ? idea.Body[..140].TrimEnd() + "…" : idea.Body, 11.5); body.Margin = new Thickness(0, 4, 0, 0); body.MaxHeight = 52; stack.Children.Add(body); }
        var foot = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        int index = Array.IndexOf(IdeaStatus.All, idea.Status);
        var moves = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(moves, Dock.Right);
        if (index > 0) moves.Children.Add(CardButton("←", "Mover para " + IdeaStatus.Label(IdeaStatus.All[index - 1]).ToLowerInvariant(), () => Try(() => Move(idea, IdeaStatus.All[index - 1]))));
        if (index < 2) moves.Children.Add(CardButton("→", "Mover para " + IdeaStatus.Label(IdeaStatus.All[index + 1]).ToLowerInvariant(), () => Try(() => Move(idea, IdeaStatus.All[index + 1]))));
        foot.Children.Add(moves);
        var vote = CardButton((idea.Voted ? "▲ " : "△ ") + idea.Votes, idea.Voted ? "Remover voto" : "Votar", () => Try(() => team.ToggleVote(idea.Id)));
        if (idea.Voted) vote.BorderBrush = Brush("#EDEDED");
        var meta = new StackPanel { Orientation = Orientation.Horizontal }; meta.Children.Add(vote);
        if (idea.Assignee is not null && idea.AssigneeId is long who)
        {
            var a = Avatar(idea.Assignee, who, 20, null); a.Margin = new Thickness(8, 0, 0, 0); a.ToolTip = "Responsável: " + idea.Assignee; meta.Children.Add(a);
        }
        foot.Children.Add(meta);
        stack.Children.Add(foot);
        var card = new Border { Background = selected ? Brush("#1A1A1A") : Brush("#141414"), BorderBrush = Brush(selected ? "#EDEDED" : "#262626"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8), Child = stack, Cursor = Cursors.Hand };
        card.MouseEnter += (_, _) => { if (editingIdea?.Id != idea.Id) card.BorderBrush = Brush("#3A3A3A"); };
        card.MouseLeave += (_, _) => { if (editingIdea?.Id != idea.Id) card.BorderBrush = Brush("#262626"); };
        card.MouseLeftButtonUp += (_, _) => { editingIdea = idea; newIdeaStatus = null; RenderBoard(true); };
        return card;
    }
    private Button CardButton(string label, string tip, Action action)
    {
        var button = new Button { Content = Mono(label, 10, "#CFCFCF"), ToolTip = tip, Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(label is "←" or "→" ? 4 : 0, 0, 0, 0) };
        button.Click += (_, _) => action(); return button;
    }
    private Task Move(IdeaDto idea, string status) => team.SaveIdea(new IdeaInput(idea.Id, idea.ChannelId, idea.Title, idea.Body, status, idea.Kind, idea.Priority, idea.AssigneeId));
    private static ComboBox Choice(IEnumerable<(string Value, string Label)> options, string? selected)
    {
        var box = new ComboBox { Margin = new Thickness(0, 6, 0, 12) };
        foreach (var (value, label) in options) { var item = new ComboBoxItem { Content = label, Tag = value }; box.Items.Add(item); if (value == selected) box.SelectedItem = item; }
        if (box.SelectedItem is null && box.Items.Count > 0) box.SelectedIndex = 0;
        return box;
    }
    private UIElement IdeaEditor()
    {
        var idea = editingIdea; string status = idea?.Status ?? newIdeaStatus ?? IdeaStatus.Backlog;
        var form = new StackPanel { Margin = new Thickness(16) };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var close = new Button { Content = Mono("✕", 11, "#8C8C8C"), Style = (Style)FindResource("GhostButton"), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0), ToolTip = "Fechar" };
        close.Click += (_, _) => { editingIdea = null; newIdeaStatus = null; RenderBoard(false); };
        DockPanel.SetDock(close, Dock.Right); head.Children.Add(close);
        head.Children.Add(Mono(idea is null ? "NOVO ITEM" : "EDITAR · #" + idea.Id, 10.5, "#EDEDED"));
        form.Children.Add(head);
        var title = new TextBox { Text = idea?.Title ?? "", MaxLength = 140, Margin = new Thickness(0, 6, 0, 12) };
        var body = new TextBox { Text = idea?.Body ?? "", MaxLength = 8000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 12) };
        var kind = Choice([(IdeaKind.Idea, "Ideia"), (IdeaKind.Task, "Tarefa"), (IdeaKind.Bug, "Problema")], idea?.Kind ?? IdeaKind.Idea);
        var priority = Choice([(IdeaPriority.Low, "Baixa"), (IdeaPriority.Normal, "Normal"), (IdeaPriority.High, "Alta")], idea?.Priority ?? IdeaPriority.Normal);
        var state = Choice(IdeaStatus.All.Select(s => (s, IdeaStatus.Label(s)[..1] + IdeaStatus.Label(s)[1..].ToLowerInvariant())), status);
        var owner = Choice(new[] { ("", "Ninguém") }.Concat(members.Select(m => (m.UserId.ToString(), m.Name + (m.UserId == team.User?.Id ? " (você)" : "")))), idea?.AssigneeId?.ToString() ?? "");
        form.Children.Add(Mono("TÍTULO", 10, "#5C5C5C")); form.Children.Add(title);
        form.Children.Add(Mono("DESCRIÇÃO", 10, "#5C5C5C")); form.Children.Add(body);
        var pair = new UniformGrid { Columns = 2 };
        var k = Stack(Mono("TIPO", 10, "#5C5C5C"), kind); k.Margin = new Thickness(0, 0, 6, 0); pair.Children.Add(k);
        pair.Children.Add(Stack(Mono("PRIORIDADE", 10, "#5C5C5C"), priority)); form.Children.Add(pair);
        form.Children.Add(Mono("STATUS", 10, "#5C5C5C")); form.Children.Add(state);
        form.Children.Add(Mono("RESPONSÁVEL", 10, "#5C5C5C")); form.Children.Add(owner);
        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        actions.Children.Add(Small(idea is null ? "Criar" : "Salvar", () => Try(async () =>
        {
            if (channel is null) return;
            if (title.Text.Trim().Length == 0) { Status("Dê um título ao item."); title.Focus(); return; }
            string Tag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            long? assignee = long.TryParse(Tag(owner), out long id) ? id : null;
            var saved = await team.SaveIdea(new IdeaInput(idea?.Id ?? 0, channel.Id, title.Text, body.Text, Tag(state), Tag(kind), Tag(priority), assignee));
            int index = ideas.FindIndex(i => i.Id == saved.Id); if (index >= 0) ideas[index] = saved; else ideas.Insert(0, saved);
            editingIdea = null; newIdeaStatus = null; RenderBoard(false); Status(idea is null ? "Item criado no quadro." : "Item atualizado.");
        }), true));
        if (idea is not null)
        {
            bool armed = false; Button? delete = null;
            delete = Small("Excluir", () =>
            {
                if (!armed) { armed = true; delete!.Content = "Confirmar exclusão"; delete.Foreground = (Brush)FindResource("Danger"); return; }
                Try(async () => { await team.DeleteIdea(idea.Id); ideas.RemoveAll(i => i.Id == idea.Id); editingIdea = null; RenderBoard(false); Status("Item excluído."); });
            });
            actions.Children.Add(delete);
        }
        form.Children.Add(actions);
        if (idea is not null)
            form.Children.Add(Spaced(Mono($"CRIADO POR {idea.Author.ToUpperInvariant()} · {idea.Created.ToLocalTime():dd/MM HH:mm}\nATUALIZADO {idea.Updated.ToLocalTime():dd/MM HH:mm} · {idea.Votes} VOTO{(idea.Votes == 1 ? "" : "S")}", 9.5, "#4A4A4A"), 0, 12, 0, 0));
        Dispatcher.BeginInvoke(() => { if (idea is null) title.Focus(); }, DispatcherPriority.Input);
        return new Border { Background = Brush("#0C0C0C"), BorderBrush = Brush("#3A3A3A"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = form } };
    }

    // ---------- Conta nas configurações ----------
    private UIElement AccountCard()
    {
        var row = new DockPanel();
        var avatar = Avatar(team.User!.Name, team.User.Id, 56, connectionLabel == "CONECTADO"); avatar.Margin = new Thickness(0, 0, 16, 0); row.Children.Add(avatar);
        row.Children.Add(Stack(Text(team.User.Name, 18, true), Muted(team.User.Email), Mono("SERVIDOR " + team.Server.ToUpperInvariant(), 9.5, "#4A4A4A")));
        var signOut = Button("Sair da conta", () => Try(SignOut)); signOut.Foreground = (Brush)FindResource("Danger");
        return Card(Stack(Mono("MINHA CONTA", 11, "#EDEDED"), Spaced(row, 0, 12, 0, 0), Actions(signOut)));
    }

    private void ShutdownTeam()
    {
        voiceTimer.Stop(); voice?.Dispose(); voice = null;
        try { team.LeaveCall().Wait(1500); } catch (Exception) { }
        try { team.DisposeAsync().AsTask().Wait(2000); } catch (Exception) { }
    }

    // ---------- Teste de interface com servidor real (PCBOOST_TEAM_TEST=http://localhost:5080) ----------
    private async Task RunTeamUiTest(string server)
    {
        string tag = Guid.NewGuid().ToString("N")[..10];
        await team.SignIn(server, $"ui-{tag}@pcboost.test", "senha-de-teste-123", "Ana Ribeiro", register: true, remember: false);
        HideLogin(); UpdateAccount();
        var group = await team.CreateGroup("Estúdio Aurora");
        await team.CreateChannel(group.Id, "design", ChannelKind.Text);
        await team.CreateChannel(group.Id, "Reunião", ChannelKind.Voice);
        // Um segundo usuário de verdade (sem áudio) para o servidor parecer vivo.
        await using var friend = new TeamClient(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pcboost-ui-friend-" + tag));
        await friend.SignIn(server, $"ui2-{tag}@pcboost.test", "senha-de-teste-123", "Bruno Costa", register: true, remember: false);
        await friend.JoinGroup(group.Code);
        await LoadGroups(group.Id);
        var text = channels.First(c => c.Kind == ChannelKind.Text && c.Name == "geral");
        var board = channels.First(c => c.Kind == ChannelKind.Board); var voiceChannel = channels.First(c => c.Kind == ChannelKind.Voice && c.Name == "Geral");
        await team.Send(text.Id, "Bom dia, pessoal! Hoje fechamos o roadmap do mês.");
        await team.Send(text.Id, "Subi as ideias no #ideias — votem nas que fazem sentido.");
        await friend.Send(text.Id, "Boa! Às 15h entro na voz pra gente revisar.");
        await friend.Send(text.Id, "Antes disso vou rodar o Boost e testar o jogo novo 😄");
        for (int i = 0; i < 50 && chat.Count < 4; i++) await Task.Delay(100);
        if (chat.Count < 4) throw new Exception("Chat messages did not arrive through the hub.");
        await friend.JoinCall(voiceChannel.Id);
        for (int i = 0; i < 30 && (voiceStates.GetValueOrDefault(voiceChannel.Id)?.Count ?? 0) == 0; i++) await Task.Delay(100);
        RenderSidebar(); await CapturePreview("groups-preview.png");
        await team.SaveIdea(new IdeaInput(0, board.Id, "Modo foco com timer Pomodoro", "Fechar distrações durante 25 min e avisar o servidor.", IdeaStatus.Backlog, IdeaKind.Idea, IdeaPriority.High, null));
        await team.SaveIdea(new IdeaInput(0, board.Id, "Painel de metas da semana", "", IdeaStatus.Backlog, IdeaKind.Idea, IdeaPriority.Normal, null));
        var doing = await team.SaveIdea(new IdeaInput(0, board.Id, "Revisar onboarding de novos membros", "Checklist + vídeo curto.", IdeaStatus.Doing, IdeaKind.Task, IdeaPriority.Normal, team.User!.Id));
        await friend.SaveIdea(new IdeaInput(0, board.Id, "Áudio cortando em redes lentas", "", IdeaStatus.Doing, IdeaKind.Bug, IdeaPriority.High, friend.User!.Id));
        await team.SaveIdea(new IdeaInput(0, board.Id, "Definir nome do projeto", "", IdeaStatus.Done, IdeaKind.Task, IdeaPriority.Low, team.User.Id));
        await friend.ToggleVote(doing.Id);
        await SelectChannel(board, join: false);
        for (int i = 0; i < 30 && ideas.Count < 5; i++) await Task.Delay(100);
        if (ideas.Count < 5) throw new Exception("Board items missing.");
        await CapturePreview("ideas-preview.png");
        editingIdea = ideas.First(x => x.Id == doing.Id); RenderBoard(true); await CapturePreview("idea-editor-preview.png");
        await SelectChannel(voiceChannel, join: false); await CapturePreview("voice-preview.png");
        if (voiceHost is null) throw new Exception("Voice view missing.");
        await SelectChannel(text, join: false);
        await friend.LeaveCall(); await friend.SignOut();
        await team.SignOut(); groups = []; current = null; channel = null; UpdateAccount();
        teamTestNote = " Discord-style servers against live server: register, server with default + created channels, second real user, 4 chat messages via hub, voice presence in sidebar and tiles, 5 board items + vote, previews captured.";
    }
}
