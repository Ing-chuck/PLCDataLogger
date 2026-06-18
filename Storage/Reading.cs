namespace PlcDataLogger.Storage;

/// <summary>
/// A single tag value change flowing from an OPC UA notification toward the database.
/// Numeric values go in <see cref="Value"/>; non-numeric values in <see cref="ValueText"/>.
/// </summary>
public sealed record Reading(
    int TagId,
    DateTime TsSourceUtc,
    DateTime TsReceivedUtc,
    double? Value,
    string? ValueText,
    string Quality,
    string DataTypeName);
