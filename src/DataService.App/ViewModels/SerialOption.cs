namespace DataService.App.ViewModels;

/// <summary>
/// Label/value pair for the connection parameter drop-downs. The label is what the
/// combo box renders, so "1.5" shows instead of the enum name "OnePointFive".
/// </summary>
public sealed record SerialOption<TValue>(string Label, TValue Value)
{
    public override string ToString() => Label;
}
