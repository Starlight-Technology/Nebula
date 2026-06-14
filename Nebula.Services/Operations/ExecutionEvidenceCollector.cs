using System.Security.Cryptography;
using System.Text;

using Nebula.Core.Operations;

namespace Nebula.Services.Operations;

public sealed class ExecutionEvidenceCollector : IExecutionEvidenceCollector
{
    public ExecutionEvidence Collect(ExecutionEvidenceInput input)
    {
        var contentHash = input.Content is null
            ? null
            : Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(input.Content)));

        return new ExecutionEvidence(
            Guid.NewGuid(),
            input.SessionId,
            input.StepId,
            input.OperationKind,
            input.Command,
            input.FilePath,
            contentHash,
            input.Executed,
            input.ExitCode,
            input.StdOut,
            input.StdErr,
            input.Success,
            DateTimeOffset.UtcNow);
    }
}
