namespace ScreenSub.Core.Models;

public record struct ScreenRegion(int X, int Y, int Width, int Height)
{
    public static readonly ScreenRegion Empty = new(0, 0, 0, 0);

    public readonly bool IsEmpty => Width <= 0 || Height <= 0;

    public readonly string FormattedSize => $"{Math.Max(0, Width)} × {Math.Max(0, Height)}";

    public override readonly string ToString() => $"({X}, {Y}, {Width}x{Height})";
}
