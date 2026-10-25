#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure;

/// <summary>
/// What a Lattice verb (find, write or show lattice) answers, with no Lattice type in it: the reply, or the problems
/// with the call's JSON (each "path: problem"; the call is refused as invalid_argument then).
/// </summary>
internal sealed class LatticeAnswer
{
    internal LatticeAnswer(JObject reply)
    {
        Reply = reply;
        Problems = new List<string>();
    }

    internal LatticeAnswer(List<string> problems)
    {
        Reply = new JObject();
        Problems = problems;
    }

    internal JObject Reply { get; }

    internal List<string> Problems { get; }

    internal bool Refused => Problems.Count > 0;
}
