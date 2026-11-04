using FluentAssertions;
using ScreenSub.Capture;
using Xunit;

namespace UnitTests;

public class FrameChangeDetectorTests
{
    private readonly FrameChangeDetector _detector = new();

    [Fact]
    public void HasFrameChanged_FirstFrame_ShouldReturnTrue()
    {
        byte[] frame = [1, 2, 3, 4, 5, 6, 7, 8];
        bool changed = _detector.HasFrameChanged(frame);
        changed.Should().BeTrue();
    }

    [Fact]
    public void HasFrameChanged_IdenticalFrame_ShouldReturnFalse()
    {
        byte[] frame = new byte[2048];
        Array.Fill(frame, (byte)100);

        _detector.HasFrameChanged(frame); // first
        bool second = _detector.HasFrameChanged(frame); // identical
        second.Should().BeFalse();
    }

    [Fact]
    public void HasFrameChanged_SignificantlyDifferentFrame_ShouldReturnTrue()
    {
        byte[] frame1 = new byte[2048];
        Array.Fill(frame1, (byte)10);

        byte[] frame2 = new byte[2048];
        Array.Fill(frame2, (byte)200);

        _detector.HasFrameChanged(frame1);
        bool changed = _detector.HasFrameChanged(frame2);
        changed.Should().BeTrue();
    }

    [Fact]
    public void Reset_ShouldForceNextFrameAsChanged()
    {
        byte[] frame = new byte[2048];
        Array.Fill(frame, (byte)50);

        _detector.HasFrameChanged(frame);
        _detector.Reset();
        bool changed = _detector.HasFrameChanged(frame);
        changed.Should().BeTrue();
    }
}
