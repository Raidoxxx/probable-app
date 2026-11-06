using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PCBoost;

// Ilustrações em matriz de pontos geradas proceduralmente (sem imagens externas).
public static class DotArt
{
    private sealed class Canvas(int width, int height)
    {
        public readonly int Width = width, Height = height;
        public readonly byte[] Pixels = new byte[width * height * 4];
        public void Dot(double x, double y, double alpha, int size = 1)
        {
            int px = (int)x, py = (int)y; byte a = (byte)Math.Clamp(alpha * 255, 0, 255);
            for (int dy = 0; dy < size; dy++)
                for (int dx = 0; dx < size; dx++)
                {
                    int X = px + dx, Y = py + dy; if (X < 0 || Y < 0 || X >= Width || Y >= Height) continue;
                    int i = (Y * Width + X) * 4; if (Pixels[i + 3] >= a) continue;
                    Pixels[i] = Pixels[i + 1] = Pixels[i + 2] = Pixels[i + 3] = a; // branco pré-multiplicado
                }
        }
        public BitmapSource Bitmap()
        {
            var bitmap = new WriteableBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, Width, Height), Pixels, Width * 4, 0); bitmap.Freeze(); return bitmap;
        }
    }

    private static double Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 982451653);
            h = (h ^ (h >> 13)) * 1274126177; h ^= h >> 16; return (h & 0xFFFFFF) / (double)0x1000000;
        }
    }
    private static double Noise(double x, double y, int seed)
    {
        int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y); double xf = x - xi, yf = y - yi;
        double u = xf * xf * (3 - 2 * xf), v = yf * yf * (3 - 2 * yf);
        double a = Hash(xi, yi, seed), b = Hash(xi + 1, yi, seed), c = Hash(xi, yi + 1, seed), d = Hash(xi + 1, yi + 1, seed);
        return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
    }
    private static double Fbm(double x, double y, int seed) => 0.55 * Noise(x, y, seed) + 0.3 * Noise(x * 2.1, y * 2.1, seed + 7) + 0.15 * Noise(x * 4.3, y * 4.3, seed + 13);

    // Colunas clássicas em ruína, como fundo da tela de entrada (imagem parada).
    public static BitmapSource Columns(int width, int height, int seed = 3)
    {
        width = Math.Max(16, width); height = Math.Max(16, height);
        var canvas = new Canvas(width, height);
        foreach (var p in ColumnDots(width, height, seed)) canvas.Dot(p.X, p.Y, p.Alpha, p.Size);
        return canvas.Bitmap();
    }

    // Um ponto da arte das colunas, com a coluna a que pertence (para animar cada uma separadamente).
    public readonly record struct ColumnDot(int X, int Y, double Alpha, int Size, int Column);

    public static List<ColumnDot> ColumnDots(int width, int height, int seed = 3)
    {
        var dots = new List<ColumnDot>(width * height / 30); int step = 3;
        var columns = new[] { (X: 0.17, Top: 0.02, Half: 0.07, Broken: false), (X: 0.52, Top: 0.16, Half: 0.058, Broken: true), (X: 0.85, Top: 0.01, Half: 0.07, Broken: false) };
        double floor = height * 0.985;
        for (int gy = 0; gy < height; gy += step)
            for (int gx = 0; gx < width; gx += step)
            {
                double best = 0; int owner = 0;
                for (int ci = 0; ci < columns.Length; ci++)
                {
                    var col = columns[ci];
                    double cx = col.X * width, half = col.Half * width, top = col.Top * height;
                    double capH = height * 0.13, baseH = height * 0.065, shaftTop = top + capH, shaftBottom = floor - baseH;
                    double dx = gx - cx, density = 0;
                    if (gy >= shaftTop && gy < shaftBottom && Math.Abs(dx) < half)
                    {
                        double u = dx / half, theta = Math.Asin(Math.Clamp(u, -1, 1));
                        double flute = Math.Pow(Math.Abs(Math.Cos(theta * 9)), 5);
                        double light = 0.45 + 0.55 * Math.Max(0, Math.Cos(theta + 0.5));
                        density = (0.12 + 0.78 * flute) * light + (Math.Abs(u) > 0.9 ? 0.5 : 0);
                        if (gy < shaftTop + height * 0.015 || gy > shaftBottom - height * 0.02) density = 0.85; // anéis
                    }
                    else if (gy >= top && gy < shaftTop)
                    {
                        double t = (shaftTop - gy) / capH, w = half * (1.0 + 0.75 * Math.Pow(t, 1.4));
                        if (Math.Abs(dx) < w) density = 0.25 + 0.7 * Fbm(gx * 0.035, gy * 0.035, seed + 1) * (1 - 0.4 * Math.Abs(dx) / w);
                        if (col.Broken && t > 0.4) density *= Fbm(gx * 0.05, gy * 0.02, seed + 5) > 0.5 ? 1 : 0.1;
                    }
                    else if (gy >= shaftBottom && gy < floor)
                    {
                        double t = (gy - shaftBottom) / baseH, w = half * (t < 0.45 ? 1.25 : t < 0.75 ? 1.5 : 1.75);
                        if (Math.Abs(dx) < w) density = 0.55 + 0.35 * Noise(gx * 0.1, gy * 0.1, seed + 2);
                        else if (Math.Abs(dx) < w * 1.6 && Hash(gx, gy, seed + 9) < 0.18 * (1 - (Math.Abs(dx) - w) / (w * 0.6))) density = 0.9; // entulho
                    }
                    // Desgaste: faixas de pontos ausentes.
                    if (density > 0) density *= Fbm(gx * 0.012 + col.X * 40, gy * 0.006, seed + 3) > 0.33 ? 1 : 0.15;
                    // Andaimes: linhas verticais esparsas ao redor.
                    if (density == 0 && gy > top + capH * 0.4 && gy < floor && (Math.Abs(Math.Abs(dx) - half * 1.35) < step * 0.6 || Math.Abs(Math.Abs(dx) - half * 1.6) < step * 0.6))
                        density = Noise(gy * 0.02, dx > 0 ? 1 : 2, seed + 11) > 0.62 ? 0.5 : 0;
                    if (density > best) { best = density; owner = ci; }
                }
                if (best > 0 && Hash(gx, gy, seed) < best)
                {
                    double bright = Hash(gx, gy, seed + 4);
                    dots.Add(new ColumnDot(gx, gy, 0.3 + 0.65 * bright * Math.Min(1, best + 0.3), bright > 0.82 && best > 0.6 ? 2 : 1, owner));
                }
            }
        return dots;
    }

    // Colunas animadas: cada uma sobe do chão, fica de pé, desmorona de cima para baixo e se reconstrói.
    public sealed class ColumnsAnimation
    {
        private const double Cycle = 15;                          // segundos por ciclo de cada coluna
        private static readonly double[] Offsets = [0.0, 0.36, 0.68]; // colunas defasadas, nunca todas juntas
        private readonly struct Particle(ColumnDot dot, float height, float jitter, float spread, float vx, float vy)
        {
            public readonly ColumnDot Dot = dot; public readonly float Height = height, Jitter = jitter, Spread = spread, Vx = vx, Vy = vy;
        }
        private readonly Particle[] particles;
        private readonly Canvas canvas;
        private readonly double floor, gravity;
        public WriteableBitmap Bitmap { get; }
        public int Width { get; }
        public int Height { get; }

        public ColumnsAnimation(int width, int height, int seed = 3)
        {
            Width = Math.Max(16, width); Height = Math.Max(16, height);
            canvas = new Canvas(Width, Height);
            floor = Height * 0.985; gravity = Height * 1.6;
            var dots = ColumnDots(Width, Height, seed);
            particles = new Particle[dots.Count];
            for (int i = 0; i < dots.Count; i++)
            {
                var d = dots[i];
                particles[i] = new Particle(d, (float)Math.Clamp((floor - d.Y) / floor, 0, 1),
                    (float)Hash(d.X, d.Y, seed + 21), (float)((Hash(d.X, d.Y, seed + 22) - 0.5) * Width * 0.09),
                    (float)((Hash(d.X, d.Y, seed + 23) - 0.5) * Width * 0.12), (float)(-Hash(d.X, d.Y, seed + 24) * Height * 0.25));
            }
            Bitmap = new WriteableBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32, null);
        }

        private static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);

        // Desenha o quadro do instante t (segundos). still = colunas inteiras, sem movimento.
        public void Render(double t, bool still = false)
        {
            Array.Clear(canvas.Pixels);
            foreach (ref readonly var p in particles.AsSpan())
            {
                var d = p.Dot;
                if (still) { canvas.Dot(d.X, d.Y, d.Alpha, d.Size); continue; }
                double s = ((t / Cycle + Offsets[d.Column % Offsets.Length]) % 1) * Cycle;
                double x = d.X, y = d.Y, alpha = d.Alpha;
                if (s < 4.5)
                {
                    // Construção: os pontos sobem do chão para o lugar, de baixo para cima.
                    double start = 0.2 + p.Height * 3.0 + p.Jitter * 0.5, k = (s - start) / 0.9;
                    if (k <= 0) continue;
                    if (k < 1)
                    {
                        double e = EaseOut(k);
                        x = d.X + p.Spread * (1 - e); y = floor + (d.Y - floor) * e; alpha *= Math.Min(1, k * 1.6);
                    }
                }
                else if (s < 9)
                {
                    alpha *= 0.86 + 0.14 * Math.Sin(t * 2.2 + p.Jitter * 40); // brilho leve enquanto está de pé
                }
                else
                {
                    // Destruição: quebra do topo para a base e os pontos caem com gravidade até virar entulho.
                    double broke = 9 + (1 - p.Height) * 2.2 + p.Jitter * 0.7, dt = s - broke;
                    if (dt > 0)
                    {
                        x = d.X + p.Vx * dt; y = d.Y + p.Vy * dt + 0.5 * gravity * dt * dt;
                        double rest = floor - p.Jitter * Height * 0.03;
                        if (y > rest) y = rest;
                        alpha *= Math.Clamp(1 - (dt - 0.5) / 1.8, 0, 1);
                        if (alpha <= 0.01) continue;
                    }
                }
                canvas.Dot(x, y, alpha, d.Size);
            }
            Bitmap.WritePixels(new Int32Rect(0, 0, Width, Height), canvas.Pixels, Width * 4, 0);
        }
    }

    // Esfera de pontos; o hemisfério inferior acende conforme o uso (0..1). Chão de partículas embaixo.
    public static BitmapSource Sphere(int width, int height, double angle, double fill, int seed = 5)
    {
        width = Math.Max(16, width); height = Math.Max(16, height);
        var canvas = new Canvas(width, height);
        double cx = width / 2.0, cy = height * 0.44, r = Math.Min(width * 0.36, height * 0.36);
        const int count = 2600; double golden = Math.PI * (3 - Math.Sqrt(5));
        double level = -1 + 2 * Math.Clamp(fill, 0, 1), tilt = 0.32; // altura do "líquido" em coordenadas da esfera (-1 base .. 1 topo)
        (double X, double Y, double Depth) Project(double x, double y, double z)
        {
            double y2 = y * Math.Cos(tilt) - z * Math.Sin(tilt), z2 = y * Math.Sin(tilt) + z * Math.Cos(tilt);
            return (cx + x * r, cy - y2 * r, (z2 + 1) / 2);
        }
        for (int i = 0; i < count; i++)
        {
            double y = 1 - 2 * (i + 0.5) / count, radius = Math.Sqrt(1 - y * y), phi = i * golden + angle;
            var p = Project(Math.Cos(phi) * radius, y, Math.Sin(phi) * radius);
            bool filled = y <= level;
            double alpha = (filled ? 0.5 + 0.5 * p.Depth : 0.06 + 0.3 * p.Depth) * (0.75 + 0.25 * Hash(i, 0, seed));
            canvas.Dot(p.X, p.Y, alpha, filled && p.Depth > 0.6 ? 2 : 1);
        }
        // Anel do nível de preenchimento.
        double ringRadius = Math.Sqrt(Math.Max(0, 1 - level * level));
        for (int i = 0; i < 260; i++)
        {
            double t = i / 260.0 * Math.PI * 2;
            var p = Project(Math.Cos(t + angle) * ringRadius, level, Math.Sin(t + angle) * ringRadius);
            canvas.Dot(p.X, p.Y, p.Depth > 0.5 ? 0.95 : 0.3, p.Depth > 0.5 ? 2 : 1);
        }
        // Chão de partículas.
        double groundY = cy + r * 1.18;
        for (int i = 0; i < 1800; i++)
        {
            double a = Hash(i, 1, seed) * Math.PI * 2, d = Math.Pow(Hash(i, 2, seed), 0.6);
            double gx = cx + Math.Cos(a) * d * width * 0.42, gy = groundY + Math.Sin(a) * d * height * 0.07;
            canvas.Dot(gx, gy, (1 - d) * 0.75 + 0.1, Hash(i, 3, seed) > 0.9 ? 2 : 1);
        }
        return canvas.Bitmap();
    }
}
