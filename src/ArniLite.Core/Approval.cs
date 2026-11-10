using System.Threading;
using System.Threading.Tasks;

namespace ArniLite.Core
{
    public interface IUserApproval
    {
        bool AllowSideEffectsForSession { get; }

        Task<bool> ApproveAsync(string title, string body, CancellationToken cancellationToken);
    }
}
