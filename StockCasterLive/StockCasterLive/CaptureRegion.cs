namespace StockCasterLive;

public sealed record CaptureRegion(int X, int Y, int Width, int Height);

/// <summary>A crop expressed relative to a window-capture frame, with each value normalized to 0..1.</summary>
public sealed record WindowCaptureCrop(double X, double Y, double Width, double Height);
