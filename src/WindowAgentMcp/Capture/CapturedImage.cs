namespace WindowAgentMcp.Capture;

public sealed record CapturedImage(byte[] PngBytes, int Width, int Height, string CoordinateSpace, uint Dpi, string TargetTitle);
