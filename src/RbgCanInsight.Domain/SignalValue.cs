namespace RbgCanInsight.Domain;

/// <summary>One decoded sample of a database signal at a specific point of the trace.</summary>
public readonly record struct SignalValue(
    string Network,
    string Message,
    string Signal,
    double RawValue,
    double PhysicalValue,
    string Unit,
    long TimestampNanoseconds,
    int Channel,
    uint FrameIdentifier);
