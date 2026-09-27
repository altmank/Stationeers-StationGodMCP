#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// One item of a batch reply. Index and ok always come first on the wire; a success adds its own fields after them,
/// a failure adds error.
/// </summary>
internal abstract class BatchItemView
{
    protected BatchItemView(int index, bool ok)
    {
        Index = index;
        Ok = ok;
    }

    [JsonProperty(Order = -3)]
    public int Index { get; }

    [JsonProperty(Order = -2)]
    public bool Ok { get; }
}

internal sealed class BatchErrorView : BatchItemView
{
    internal BatchErrorView(int index, ErrorView error) : base(index, ok: false)
    {
        Error = error;
    }

    public ErrorView Error { get; }

    internal static BatchErrorView Of(int index, ApiException exception) =>
        new BatchErrorView(index, new ErrorView(exception.Code, exception.Message));
}

/// <summary>A batch reply: every item, and how many succeeded and failed.</summary>
internal sealed class BatchResultView
{
    internal BatchResultView(List<BatchItemView> results, int successCount)
    {
        Results = results;
        Count = results.Count;
        SuccessCount = successCount;
        ErrorCount = results.Count - successCount;
    }

    public List<BatchItemView> Results { get; }

    public int Count { get; }

    public int SuccessCount { get; }

    public int ErrorCount { get; }
}

/// <summary>Collects a batch's items in order, counting the successes.</summary>
internal sealed class BatchBuilder
{
    private readonly List<BatchItemView> _results;
    private int _successCount;

    internal BatchBuilder(int capacity)
    {
        _results = new List<BatchItemView>(capacity);
    }

    internal void Succeeded(BatchItemView item)
    {
        _results.Add(item);
        _successCount++;
    }

    internal void Failed(int index, ApiException error) => _results.Add(BatchErrorView.Of(index, error));

    /// <summary>A failure that names more than its error, e.g. the item a refused move was about.</summary>
    internal void Failed(BatchItemView failure) => _results.Add(failure);

    internal BatchResultView Build() => new BatchResultView(_results, _successCount);
}
