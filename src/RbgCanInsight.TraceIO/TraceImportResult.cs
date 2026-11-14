using RbgCanInsight.Domain;

namespace RbgCanInsight.TraceIO;

/// <summary>Everything one import produced: the CAN frames (unchanged pipeline), the LIN / FlexRay / CAN XL
/// side collection and the import health snapshot.</summary>
public sealed record TraceImportResult(
    IReadOnlyList<CanFrame> CanFrames,
    BusFrameCollection BusFrames,
    TraceImportHealthSnapshot Health);

public static partial class TraceImporter
{
    /// <summary>Convenience wrapper that materialises the CAN frames and the bus side collection.
    /// Existing <see cref="ReadAsync"/> call sites are unaffected.</summary>
    public static async Task<TraceImportResult> ReadAllAsync(string path, CancellationToken cancellationToken = default)
    {
        var health = new TraceImportHealth();
        var frames = new List<CanFrame>();
        await foreach (var frame in ReadAsync(path, cancellationToken, health))
            frames.Add(frame);
        return new TraceImportResult(frames, health.BusFrames, health.Snapshot());
    }
}
