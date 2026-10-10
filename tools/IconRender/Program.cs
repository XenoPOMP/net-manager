using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

public class IconApp : Application { }

// Source of truth for the artwork is src/NetManager.App/Assets/icon.svg — if it changes, copy its path data into Build() below.
// Run tools/make-icon.sh to regenerate Assets/icon.png and Assets/app.ico.
// Draws Icon.svg (512x512: gradient rounded square + white stroked monitor/terminal glyph) at several sizes, with transparency.
class P
{
    static Control Build(int size)
    {
        var canvas = new Canvas { Width = 512, Height = 512 };
        canvas.Children.Add(new Border
        {
            Width = 512, Height = 512, CornerRadius = new CornerRadius(80),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#061690"), 0), new GradientStop(Color.Parse("#02062A"), 1) },
            },
        });
        Avalonia.Controls.Shapes.Path Stroke(string data) => new()
        {
            Data = Geometry.Parse(data), Stroke = Brushes.White, StrokeThickness = 15,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
        };
        canvas.Children.Add(Stroke("M189.333 406H322.667M256 339.333V406M122.667 106H389.333C407.743 106 422.667 120.924 422.667 139.333V306C422.667 324.41 407.743 339.333 389.333 339.333H122.667C104.257 339.333 89.3333 324.41 89.3333 306V139.333C89.3333 120.924 104.257 106 122.667 106Z"));
        canvas.Children.Add(Stroke("M256 227H256.091M289.333 227H289.424M297.667 185.333L339.333 227L297.667 268.667M214.333 185.333L172.667 227L214.333 268.667M222.667 227H222.758"));
        return new Viewbox { Width = size, Height = size, Stretch = Stretch.Uniform, Child = canvas };
    }

    static int Main(string[] args)
    {
        var outDir = args[0];
        AppBuilder.Configure<IconApp>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        Directory.CreateDirectory(outDir);
        Dispatcher.UIThread.Invoke(() =>
        {
            foreach (var size in new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256, 512 })
            {
                var v = Build(size);
                v.Measure(new Size(size, size));
                v.Arrange(new Rect(0, 0, size, size));
                using var bmp = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
                bmp.Render(v);
                bmp.Save(System.IO.Path.Combine(outDir, $"icon-{size}.png"));
            }
        });
        Console.WriteLine("done");
        return 0;
    }
}
