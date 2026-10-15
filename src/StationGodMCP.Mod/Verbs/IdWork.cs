#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// Ids answered as IdAnswers.Of answers them, each id's view built as work over frames (WorkResult): start resolves the
/// id (a refusal there, or at any step of its body, is the row's) and gives the row's reference id and the body that
/// builds its view.
/// </summary>
internal static class IdWork
{
    internal static VerbOutcome Of<TView>(PerIdList ids,
        Func<string, (string ReferenceId, Func<Answer<TView>, IEnumerable<object?>> Body)> start)
        where TView : class
    {
        Dictionary<string, Row<TView>> rows = new Dictionary<string, Row<TView>>();
        return WorkResult<bool>.Of(answer => Each(ids.Ids, start, rows, answer),
            _ => IdAnswers.Of(ids, id => rows[id].Answered()));
    }

    private static IEnumerable<object?> Each<TView>(IReadOnlyList<string> ids,
        Func<string, (string ReferenceId, Func<Answer<TView>, IEnumerable<object?>> Body)> start,
        Dictionary<string, Row<TView>> rows, Answer<bool> answer)
        where TView : class
    {
        foreach (string id in ids)
        {
            if (rows.ContainsKey(id))
            {
                continue;
            }

            (string ReferenceId, Func<Answer<TView>, IEnumerable<object?>> Body) started;
            try
            {
                started = start(id);
            }
            catch (ApiException refused)
            {
                rows[id] = new Row<TView>(null, null, refused);
                continue;
            }

            Answer<TView> view = new Answer<TView>();
            using IEnumerator<object?> steps = started.Body(view).GetEnumerator();
            ApiException? stopped = null;
            while (true)
            {
                bool more;
                try
                {
                    more = steps.MoveNext();
                }
                catch (ApiException refused)
                {
                    stopped = refused;
                    break;
                }

                if (!more)
                {
                    break;
                }

                yield return null;
            }

            rows[id] = stopped != null
                ? new Row<TView>(null, null, stopped)
                : new Row<TView>(started.ReferenceId, view.Value, null);
        }

        answer.Value = true;
    }

    // One id's row: its reference id and view, or the refusal, thrown again where the rows are built.
    private sealed class Row<TView>
        where TView : class
    {
        private readonly string? _referenceId;
        private readonly TView? _view;
        private readonly ApiException? _refused;

        internal Row(string? referenceId, TView? view, ApiException? refused)
        {
            _referenceId = referenceId;
            _view = view;
            _refused = refused;
        }

        internal (string ReferenceId, object View) Answered() =>
            _refused != null ? throw _refused : (_referenceId!, _view!);
    }
}
