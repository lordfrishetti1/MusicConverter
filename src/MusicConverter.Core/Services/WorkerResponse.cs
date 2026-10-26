using MusicConverter.Abstractions;

namespace MusicConverter.Services;

/// <summary>JSON written to stdout by a worker process: either the conversion result or a categorised failure.</summary>
public sealed record WorkerResponse(
    bool Ok, ConversionResult? Result = null, ConversionErrorCategory Category = ConversionErrorCategory.ParseError,
    string? Message = null, int? Line = null);