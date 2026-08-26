using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LabQueue.Core.Data.Converters;

/// <summary>
/// Stores enums as their lowercase name, so the database reads
/// 'confirmed' / 'member' rather than 'Confirmed' / 'Member'.
/// </summary>
public static class LowercaseEnumConverter
{
    public static ValueConverter<TEnum, string> For<TEnum>() where TEnum : struct, Enum
        => new(
            value => value.ToString().ToLowerInvariant(),
            stored => Enum.Parse<TEnum>(stored, ignoreCase: true));

    public static string[] NamesOf<TEnum>() where TEnum : struct, Enum
        => Enum.GetNames<TEnum>().Select(n => n.ToLowerInvariant()).ToArray();
}
