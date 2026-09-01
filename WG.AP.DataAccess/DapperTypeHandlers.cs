using System.Data;
using Dapper;

namespace WG.AP.DataAccess;

/// <summary>
/// Teaches Dapper how to bind <see cref="DateOnly"/> parameters.
/// </summary>
/// <remarks>
/// Without these, any insert carrying a <see cref="DateOnly"/> fails at runtime with "The member
/// InvoiceDate of type System.DateOnly cannot be used as a parameter value" — and it fails on the
/// first real invoice, because <c>InvoiceFields.InvoiceDate</c> and <c>DueDate</c> are both
/// <see cref="DateOnly"/>. Nothing at compile time hints at it.
/// <para>
/// Registration is driven from <see cref="SqlConnectionFactory"/>'s static constructor rather than
/// from DI, because every database call in this assembly opens its connection there — so there is no
/// path that can reach a query without having passed through it, and no caller has to remember to
/// initialise anything.
/// </para>
/// <para>
/// <c>DbType.Date</c> is set explicitly so the parameter matches the <c>DATE</c> columns it targets;
/// left to infer, a <see cref="DateTime"/> parameter carries a time component the column does not
/// have, which costs an implicit conversion on every comparison.
/// </para>
/// </remarks>
internal static class DapperTypeHandlers
{
    private static bool _registered;
    private static readonly object Gate = new();

    internal static void EnsureRegistered()
    {
        if (_registered)
        {
            return;
        }

        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            SqlMapper.AddTypeHandler(new DateOnlyHandler());
            SqlMapper.AddTypeHandler(new NullableDateOnlyHandler());
            _registered = true;
        }
    }

    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value.ToDateTime(TimeOnly.MinValue);
        }

        public override DateOnly Parse(object value) => value switch
        {
            DateOnly dateOnly => dateOnly,
            DateTime dateTime => DateOnly.FromDateTime(dateTime),
            string text => DateOnly.Parse(text),
            _ => throw new DataException($"Cannot convert {value.GetType()} to DateOnly.")
        };
    }

    private sealed class NullableDateOnlyHandler : SqlMapper.TypeHandler<DateOnly?>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly? value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value?.ToDateTime(TimeOnly.MinValue) ?? (object)DBNull.Value;
        }

        public override DateOnly? Parse(object? value) => value switch
        {
            null or DBNull => null,
            DateOnly dateOnly => dateOnly,
            DateTime dateTime => DateOnly.FromDateTime(dateTime),
            string text => DateOnly.Parse(text),
            _ => throw new DataException($"Cannot convert {value.GetType()} to DateOnly?.")
        };
    }
}
