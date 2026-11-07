using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PCBoost;

// Página Boost: um botão e os jogos do PC. Toda a configuração fica em AutoBoost.
public partial class MainWindow
{
    private AutoBoost? autoBoost;
    private List<GameEntry> games = [];
    private bool gamesScanned, boosting;
    private readonly Dictionary<string, (ImageSource? Color, ImageSource? Gray)> gameArt = [];
    private Button? boostButton;
    private TextBlock? boostState, gamesTitle;
    private WrapPanel? gameGrid;

    private void InitBoost()
    {
        autoBoost = new AutoBoost(store.Root);
        autoBoost.StateChanged += () => Ui(UpdateBoostState);
        Closed += (_, _) => { try { autoBoost.Deactivate(); } catch (Exception) { } };
    }

    private void BoostPage()
    {
        boostButton = null; boostState = gamesTitle = null; gameGrid = null;
        var hero = new Grid { Height = 330 };
        sphere = new Image { Stretch = Stretch.None, VerticalAlignment = VerticalAlignment.Top, Opacity = 0.55, IsHitTestVisible = false };
        sphere.SizeChanged += (_, _) => RenderSphere(false);
        hero.Children.Add(sphere);
        var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -20, 0, 0) };
        boostButton = new Button { Style = (Style)FindResource("BoostButton"), Content = "BOOST", ToolTip = "Liberar o PC agora" };
        boostButton.Click += (_, _) => Try(ToggleBoost);
        center.Children.Add(boostButton);
        boostState = Mono("", 10.5, "#8C8C8C"); boostState.HorizontalAlignment = HorizontalAlignment.Center; boostState.TextAlignment = TextAlignment.Center; boostState.Margin = new Thickness(0, 30, 0, 0);
        center.Children.Add(boostState);
        hero.Children.Add(center);
        PageHost.Children.Add(new Border { Background = Brushes.Black, BorderBrush = Brush("#262626"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 16), Child = hero });

        var head = new DockPanel { Margin = new Thickness(2, 6, 0, 14) };
        var add = new Button { Content = Mono("+ ADICIONAR JOGO", 10, "#EDEDED"), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(8, 0, 0, 0) };
        add.Click += (_, _) => AddGame();
        var rescan = new Button { Content = Mono("↻ PROCURAR DE NOVO", 10, "#8C8C8C"), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0) };
        rescan.Click += (_, _) => { gamesScanned = false; gameArt.Clear(); ScanGames(); };
        var right = new StackPanel { Orientation = Orientation.Horizontal }; right.Children.Add(rescan); right.Children.Add(add);
        DockPanel.SetDock(right, Dock.Right); head.Children.Add(right);
        gamesTitle = Mono("JOGOS", 11, "#EDEDED"); gamesTitle.VerticalAlignment = VerticalAlignment.Center; head.Children.Add(gamesTitle);
        PageHost.Children.Add(head);
        gameGrid = new WrapPanel(); PageHost.Children.Add(gameGrid);
        UpdateBoostState();
        if (gamesScanned) RenderGames(); else ScanGames();
    }

    private async Task ToggleBoost()
    {
        if (autoBoost is null || boosting) return;
        if (autoBoost.State != BoostState.Idle) { autoBoost.Deactivate(); Status(autoBoost.State == BoostState.Idle ? "Boost desligado. Tudo voltou ao normal." : autoBoost.Summary); return; }
        boosting = true; UpdateBoostState();
        try { await autoBoost.Activate(); Status("Boost ligado. Clique de novo para desligar."); }
        finally { boosting = false; UpdateBoostState(); }
    }
    private async Task BoostGame(GameEntry game)
    {
        if (autoBoost is null || boosting) return;
        boosting = true; UpdateBoostState();
        try
        {
            await autoBoost.Activate(game);
            GameLibrary.Launch(game);
            Status($"Abrindo {game.Name} com boost. Quando o jogo fechar, tudo volta ao normal sozinho.");
        }
        finally { boosting = false; UpdateBoostState(); }
    }
    private void UpdateBoostState()
    {
        UpdateBoosterPanel();
        if (boostButton is null || autoBoost is null) return;
        bool on = autoBoost.Active;
        boostButton.Content = boosting ? "· · ·" : "BOOST";
        boostButton.Background = on ? Brushes.White : Brushes.Black;
        boostButton.Foreground = on ? Brushes.Black : Brushes.White;
        boostButton.ToolTip = on ? "Desligar o boost" : "Liberar o PC agora";
        if (boostState is not null)
        {
            boostState.Text = boosting ? "LIBERANDO O PC…" : on ? "● ATIVO · " + autoBoost.Summary : "TOQUE PARA LIBERAR O PC";
            boostState.Foreground = on ? (Brush)FindResource("Accent") : Brush("#8C8C8C");
        }
    }

    // ---------- Jogos ----------
    private void ScanGames()
    {
        if (gamesTitle is not null) gamesTitle.Text = "JOGOS · PROCURANDO…";
        var custom = settings.CustomGames.ToList();
        Try(async () =>
        {
            games = await Task.Run(() => GameLibrary.Detect(custom));
            gamesScanned = true; RenderGames(); if (gameGrid is null && page != PageGroups) RenderSidebar();
        });
    }
    private void RenderGames()
    {
        if (gameGrid is null) return;
        gameGrid.Children.Clear();
        if (gamesTitle is not null) gamesTitle.Text = games.Count == 0 ? "JOGOS" : $"JOGOS · {games.Count} NESTE PC";
        if (games.Count == 0)
        {
            var empty = Stack(Text("Nenhum jogo encontrado.", 15, true), Muted("Procuro jogos da Steam, Epic, GOG, Ubisoft e Riot. Se o seu não apareceu, use + Adicionar jogo e escolha o executável."));
            empty.Margin = new Thickness(2, 4, 0, 0); gameGrid.Children.Add(empty); return;
        }
        foreach (var game in games) gameGrid.Children.Add(GameTile(game));
        Dispatcher.BeginInvoke(() => { if (page != PageGroups) RenderSidebar(); }, DispatcherPriority.ContextIdle);
    }
    private UIElement GameTile(GameEntry game)
    {
        const double width = 148, art = 206;
        var image = new Image { Width = width - 2, Height = art, Stretch = Stretch.UniformToFill };
        var placeholder = Mono(Initials(game.Name), 28, "#3A3A3A"); placeholder.HorizontalAlignment = HorizontalAlignment.Center; placeholder.VerticalAlignment = VerticalAlignment.Center;
        var picture = new Grid { Height = art, Background = Brush("#0E0E0E"), ClipToBounds = true };
        picture.Children.Add(placeholder); picture.Children.Add(image);
        var play = new Border { Background = Brushes.White, Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed, Child = Mono("▶ BOOST + JOGAR", 9.5, "#000000") };
        picture.Children.Add(play);
        var name = Mono(game.Name.ToUpperInvariant(), 10, "#EDEDED"); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; name.Margin = new Thickness(10, 8, 10, 0);
        var source = Mono(game.Source, 9, "#4A4A4A"); source.Margin = new Thickness(10, 2, 10, 9);
        var tile = new Border { Width = width, Margin = new Thickness(0, 0, 12, 12), Background = Brush("#101010"), BorderBrush = Brush("#262626"), BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
            Child = Stack(picture, name, source), ToolTip = $"{game.Name}\nClique para dar boost e abrir" };
        void Show(bool hover)
        {
            tile.BorderBrush = Brush(hover ? "#EDEDED" : "#262626"); play.Visibility = hover ? Visibility.Visible : Visibility.Collapsed;
            if (gameArt.TryGetValue(game.Key, out var a)) image.Source = hover ? a.Color : a.Gray ?? a.Color;
        }
        tile.MouseEnter += (_, _) => Show(true); tile.MouseLeave += (_, _) => Show(false);
        tile.MouseLeftButtonUp += (_, _) => Try(() => BoostGame(game));
        // A imagem carrega depois, sem travar a janela.
        Dispatcher.BeginInvoke(() =>
        {
            if (!gameArt.TryGetValue(game.Key, out var a)) { var color = GameLibrary.Art(game); a = (color, Gray(color, game.Cover is not null)); gameArt[game.Key] = a; }
            if (a.Color is not null) { image.Source = a.Gray ?? a.Color; placeholder.Visibility = Visibility.Collapsed; if (game.Cover is null) { image.Stretch = Stretch.Uniform; image.Width = image.Height = 96; } }
        }, DispatcherPriority.Background);
        return tile;
    }
    // Capas ficam em tons de cinza (combinando com o tema) e ganham cor ao passar o mouse.
    private static ImageSource? Gray(ImageSource? source, bool cover)
    {
        if (!cover || source is not BitmapSource bitmap) return null;
        try { var gray = new FormatConvertedBitmap(bitmap, PixelFormats.Gray8, null, 0); gray.Freeze(); return gray; } catch (Exception) { return null; }
    }
    private static string Initials(string name) => string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).Take(2).Select(w => char.ToUpperInvariant(w[0])));
    private void AddGame()
    {
        var dialog = new OpenFileDialog { Filter = "Jogo|*.exe", Title = "Escolha o executável do jogo" };
        if (dialog.ShowDialog() != true) return;
        if (!settings.CustomGames.Contains(dialog.FileName, StringComparer.OrdinalIgnoreCase)) { settings.CustomGames.Add(dialog.FileName); store.Save(settings); }
        gamesScanned = false; ScanGames(); Status("Jogo adicionado.");
    }
}
