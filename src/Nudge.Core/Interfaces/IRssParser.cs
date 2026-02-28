using Nudge.Core.Models;

namespace Nudge.Core.Interfaces;

public interface IRssParser
{
    Task<Result<RssParsePayload>> ParseAsync(string feedXml, CancellationToken cancellationToken = default);
}
