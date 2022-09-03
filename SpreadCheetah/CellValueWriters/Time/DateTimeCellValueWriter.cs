using SpreadCheetah.CellValueWriters.Number;
using SpreadCheetah.Helpers;
using SpreadCheetah.Styling;
using SpreadCheetah.Styling.Internal;
using System.Buffers.Text;

namespace SpreadCheetah.CellValueWriters.Time;

internal sealed class DateTimeCellValueWriter : NumberCellValueWriterBase
{
    protected override int GetStyleId(StyleId styleId) => styleId.DateTimeId;
    protected override int MaxNumberLength => ValueConstants.DoubleValueMaxCharacters;

    protected override int GetValueBytes(in DataCell cell, Span<byte> destination)
    {
        Utf8Formatter.TryFormat(cell.NumberValue.DoubleValue, destination, out var bytesWritten);
        return bytesWritten;
    }

    public override bool Equals(in CellValue value, in CellValue other) => value.DoubleValue == other.DoubleValue;
    public override int GetHashCodeFor(in CellValue value) => value.DoubleValue.GetHashCode();

    public override bool TryWriteCell(in DataCell cell, DefaultStyling? defaultStyling, SpreadsheetBuffer buffer)
    {
        var defaultStyleId = defaultStyling?.DateTimeStyleId;
        return defaultStyleId is not null
            ? TryWriteCell(cell, defaultStyleId.Value, buffer)
            : TryWriteCell(cell, buffer);
    }
}
