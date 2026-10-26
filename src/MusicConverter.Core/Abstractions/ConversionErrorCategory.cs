namespace MusicConverter.Abstractions;

public enum ConversionErrorCategory
{
    ParseError,
    MappingError,
    SerializationError,
    UnsupportedFormat,
    LimitExceeded,
    WorkerFailure
}