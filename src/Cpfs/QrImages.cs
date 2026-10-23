using System.Windows;
using System.Windows.Media;
using Cpfs.Core.Qr;

namespace Cpfs;

internal static class QrImages
{
    private const int QuietZone = 4;

    // Always dark modules on white with the four-module quiet zone, whatever the theme: scanners expect dark on light.
    // A vector drawing, so it stays sharp at any size and DPI.
    public static DrawingImage Create(string text)
    {
        var qr = QrCode.Encode(text);
        var side = qr.Size + (2 * QuietZone);
        var modules = new GeometryGroup { FillRule = FillRule.Nonzero };
        for (var y = 0; y < qr.Size; y++)
        {
            for (var x = 0; x < qr.Size; x++)
            {
                if (qr[x, y])
                {
                    modules.Children.Add(new RectangleGeometry(new Rect(x + QuietZone, y + QuietZone, 1, 1)));
                }
            }
        }

        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, side, side))));
        drawing.Children.Add(new GeometryDrawing(Brushes.Black, null, modules));
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
