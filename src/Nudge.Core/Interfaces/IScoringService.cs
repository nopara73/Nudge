using Nudge.Core.Models;

namespace Nudge.Core.Interfaces;

public interface IScoringService
{
    IntentScore Score(Show show, IReadOnlyList<string> keywords);
}
