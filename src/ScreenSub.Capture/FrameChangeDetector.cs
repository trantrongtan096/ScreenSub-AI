using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenSub.Core.Interfaces;

namespace ScreenSub.Capture;

public sealed class FrameChangeDetector : IFrameChangeDetector
{
    private const int TargetWidth = 144;
    private const int TargetHeight = 48;
    private const int TotalPixels = TargetWidth * TargetHeight; // 6912 pixels
    private const int GridCols = 9; // Odd column & row counts keep horizontal/vertical center inside a single cell
    private const int GridRows = 3;
    private const int CellWidth = TargetWidth / GridCols;   // 16
    private const int CellHeight = TargetHeight / GridRows; // 16
    private const int CellPixels = CellWidth * CellHeight;  // 256 pixels

    private byte[]? _previousPixels;
    private byte[]? _previousRawDigest;
    private int _previousRawLength;

    public bool HasFrameChanged(byte[] currentFrameBytes, double threshold = 0.02)
    {
        if (currentFrameBytes == null || currentFrameBytes.Length == 0)
        {
            return false;
        }

        byte[] currentDigest = SHA256.HashData(currentFrameBytes);
        if (_previousPixels != null
            && _previousRawDigest != null
            && currentFrameBytes.Length == _previousRawLength
            && currentDigest.AsSpan().SequenceEqual(_previousRawDigest))
        {
            return false;
        }

        byte[]? currentPixels = ExtractGrayscaleThumbnail(currentFrameBytes, TargetWidth, TargetHeight);
        if (currentPixels == null || currentPixels.Length != TotalPixels)
        {
            // Fallback only if image is genuinely undecodable and differs from previous raw bytes
            return true;
        }

        if (_previousPixels == null || _previousPixels.Length != TotalPixels)
        {
            _previousPixels = currentPixels;
            _previousRawDigest = currentDigest;
            _previousRawLength = currentFrameBytes.Length;
            return true; // First frame is always considered changed
        }

        int diffCount = 0;
        Span<int> cellDiffs = stackalloc int[GridCols * GridRows];

        for (int y = 0; y < TargetHeight; y++)
        {
            int rowOffset = y * TargetWidth;
            int cellRowOffset = (y / CellHeight) * GridCols;

            for (int x = 0; x < TargetWidth; x++)
            {
                int idx = rowOffset + x;
                if (Math.Abs(currentPixels[idx] - _previousPixels[idx]) > 14)
                {
                    diffCount++;
                    cellDiffs[cellRowOffset + (x / CellWidth)]++;
                }
            }
        }

        double diffRatio = (double)diffCount / TotalPixels;
        int maxCellDiff = 0;
        for (int c = 0; c < cellDiffs.Length; c++)
        {
            if (cellDiffs[c] > maxCellDiff) maxCellDiff = cellDiffs[c];
        }
        double maxCellRatio = (double)maxCellDiff / CellPixels;

        // Global frame change OR concentrated localized subtitle change (prevents small-font / fade-in / wide-strip dilution)
        bool localizedChanged = threshold <= 0.15 && maxCellRatio >= Math.Max(threshold * 1.35, 0.035);

        if (diffRatio >= threshold || localizedChanged)
        {
            _previousPixels = currentPixels;
            _previousRawDigest = currentDigest;
            _previousRawLength = currentFrameBytes.Length;
            return true;
        }

        _previousRawDigest = currentDigest;
        _previousRawLength = currentFrameBytes.Length;
        return false;
    }

    public void Reset()
    {
        _previousPixels = null;
        _previousRawDigest = null;
        _previousRawLength = 0;
    }

    public static byte[]? ExtractGrayscaleThumbnail(byte[] imageBytes, int targetW, int targetH)
    {
        try
        {
            using var ms = new MemoryStream(imageBytes);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];

            int srcW = frame.PixelWidth;
            int srcH = frame.PixelHeight;
            if (srcW <= 0 || srcH <= 0) return null;

            double scaleX = (double)targetW / srcW;
            double scaleY = (double)targetH / srcH;

            var scaled = new TransformedBitmap(frame, new ScaleTransform(scaleX, scaleY));
            var grayBitmap = new FormatConvertedBitmap(scaled, PixelFormats.Gray8, null, 0);

            byte[] pixels = new byte[targetW * targetH];
            grayBitmap.CopyPixels(pixels, targetW, 0);
            return pixels;
        }
        catch
        {
            return null;
        }
    }
}

